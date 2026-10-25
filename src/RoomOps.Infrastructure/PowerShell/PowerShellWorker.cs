using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RoomOps.Application.Commands;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Common;

namespace RoomOps.Infrastructure.PowerShell;

public sealed record WorkerLaunchOptions
{
    /// <summary>Path to pwsh. When null, the PATH and the default PowerShell 7 install locations are searched.</summary>
    public string? PowerShellPath { get; init; }
    /// <summary>Directory the worker script is materialized into (inside the user's profile).</summary>
    public required string WorkDirectory { get; init; }
    /// <summary>Test-only: a module that stands in for the Microsoft modules. Never set by the application composition root.</summary>
    internal string? TestModulePath { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Application window that Windows sign-in (WAM) dialogs are attached to. Zero when the host has no window.</summary>
    public nint SignInOwnerWindow { get; init; }
}

/// <summary>Holds the handle of the desktop main window once it exists, so sign-in dialogs can be shown over it.</summary>
public sealed class SignInWindow
{
    public nint Handle { get; set; }
}

public sealed record ConnectInfo(string? TenantId, string? Account, string? ModuleVersion, IReadOnlyList<string> Scopes);

/// <summary>A live service connection hosted in its own process.</summary>
public interface IServiceWorker : ICommandRunner, IAsyncDisposable
{
    bool IsAlive { get; }
    event Action<string>? Exited;
    Task StartAsync(CancellationToken cancellationToken);
    Task<ConnectInfo> ConnectAsync(ConnectRequest request, string? tenantId, Action<SignInPrompt> onPrompt, CancellationToken cancellationToken);
}

public interface IServiceWorkerFactory
{
    IServiceWorker Create(ServiceKind service);
}

/// <summary>Options are resolved per launch so a changed PowerShell path in Settings applies to the next connection.</summary>
public sealed class PowerShellWorkerFactory(Func<WorkerLaunchOptions> options, ILoggerFactory loggers) : IServiceWorkerFactory
{
    public IServiceWorker Create(ServiceKind service) => new PowerShellWorker(service, options(), loggers.CreateLogger<PowerShellWorker>());
}

/// <summary>
/// One PowerShell 7 process per service session. Communication uses the process's own anonymous stdio pipes, so there is no
/// listening endpoint other processes could connect to; every frame is additionally bound to a random per-process nonce.
/// Requests are serialized (one in flight), which also acts as natural throttling towards the service.
/// </summary>
public sealed partial class PowerShellWorker : IServiceWorker
{
    private readonly ServiceKind _service;
    private readonly WorkerLaunchOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _single = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly TaskCompletionSource<JsonObject> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private long _nextId;
    private string _prefix = string.Empty;
    private Action<string>? _diagnosticSink;
    private volatile bool _disposed;

    public PowerShellWorker(ServiceKind service, WorkerLaunchOptions options, ILogger logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    public ServiceKind Service => _service;
    public bool IsAlive => _process is { HasExited: false } && !_disposed;
    public event Action<string>? Exited;

    public static string? FindPowerShell(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Only a PowerShell 7 executable may be configured; the setting can't point the worker launcher at another program.
            return IsPowerShellExecutable(configured) && File.Exists(configured) ? configured : null;
        }
        var exe = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), exe);
            if (File.Exists(candidate)) return candidate;
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var candidate = Path.Combine(root, "PowerShell", "7", exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static bool IsPowerShellExecutable(string path)
    {
        // Split on both separators: Windows paths are validated identically on every platform.
        var name = path.Trim().Trim('"').Split('/', '\\').Last();
        return name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh", StringComparison.Ordinal);
    }

    /// <summary>Writes the embedded worker script to the work directory (if missing or changed) and returns its path and SHA-256.</summary>
    internal static (string Path, string Hash) MaterializeScript(string directory)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RoomOps.Worker.ps1")
            ?? throw new InvalidOperationException("The worker script resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"RoomOps.Worker.{hash[..12]}.ps1");
        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(Convert.FromHexString(hash)))
        {
            File.WriteAllBytes(path, bytes);
        }
        return (path, hash);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pwsh = FindPowerShell(_options.PowerShellPath)
            ?? throw new RoomOpsException(new DomainError("worker.pwshMissing", Text.L("PowerShell 7 (pwsh) was not found. Install PowerShell 7 or set its path in Settings.", "PowerShell 7 (pwsh) is niet gevonden. Installeer PowerShell 7 of stel het pad in bij Instellingen."), ErrorKind.NotConnected));
        var (script, hash) = MaterializeScript(_options.WorkDirectory);

        // Microsoft.Graph.Authentication 2.34+ always signs in through Web Account Manager on Windows and attaches the dialog to
        // GetAncestor(GetConsoleWindow(), GA_ROOTOWNER). A process created without a window has no console window, and the sign-in
        // fails with "A window handle must be configured". The Graph worker therefore gets a console whose window stays hidden;
        // the worker script makes that window owned by the RoomOps window so the dialog appears over the application.
        var needsSignInWindow = OperatingSystem.IsWindows() && _service == ServiceKind.Graph;
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = !needsSignInWindow,
            WindowStyle = needsSignInWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["ROOMOPS_WORKER_NONCE"] = _nonce;
        psi.Environment["ROOMOPS_WORKER_SERVICE"] = _service.ToString();
        psi.Environment.Remove("ROOMOPS_WORKER_TEST_MODULE");
        if (_options.TestModulePath is { } testModule) psi.Environment["ROOMOPS_WORKER_TEST_MODULE"] = testModule;
        psi.Environment.Remove("ROOMOPS_WORKER_OWNER_WINDOW");
        if (needsSignInWindow && _options.SignInOwnerWindow != 0)
        {
            psi.Environment["ROOMOPS_WORKER_OWNER_WINDOW"] = _options.SignInOwnerWindow.ToString(CultureInfo.InvariantCulture);
        }
        psi.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["POWERSHELL_UPDATECHECK"] = "Off";

        _prefix = $"@@RO1:{_nonce}:";
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => OnExited();
        if (!_process.Start())
        {
            throw new RoomOpsException(new DomainError("worker.startFailed", Text.L("The PowerShell worker could not be started.", "Het PowerShell-proces kon niet worden gestart."), ErrorKind.Internal));
        }
        _logger.LogInformation("Started {Service} worker (pid {Pid}, script {Hash})", _service, _process.Id, hash[..12]);
        _ = Task.Run(ReadStdoutAsync, CancellationToken.None);
        _ = Task.Run(ReadStderrAsync, CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);
        try
        {
            await _ready.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync();
            throw new RoomOpsException(new DomainError("worker.startTimeout", Text.L("The PowerShell worker did not start in time.", "Het PowerShell-proces is niet op tijd gestart."), ErrorKind.Transient));
        }
    }

    public async Task<ConnectInfo> ConnectAsync(ConnectRequest request, string? tenantId, Action<SignInPrompt> onPrompt, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["upn"] = request.UserPrincipalName,
            ["signIn"] = request.SignInMethod.ToString(),
            ["tenantId"] = tenantId,
        };
        _diagnosticSink = line =>
        {
            if (DeviceCodePattern().Match(line) is { Success: true } m)
            {
                onPrompt(new SignInPrompt(_service, line.Trim(), m.Groups["uri"].Value, m.Groups["code"].Value));
            }
        };
        try
        {
            var response = await SendAsync("connect", payload, _options.ConnectTimeout, cancellationToken);
            var first = response["data"] is JsonArray { Count: > 0 } arr && arr[0] is JsonObject o ? o : new JsonObject();
            return new ConnectInfo(
                first["tenantId"]?.GetValue<string>(),
                first["account"]?.GetValue<string>(),
                first["moduleVersion"]?.GetValue<string>(),
                first["scopes"] is JsonArray scopes ? scopes.Select(s => s?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList() : []);
        }
        finally
        {
            _diagnosticSink = null;
        }
    }

    public async Task<CommandResult> RunAsync(PsCommand command, CancellationToken cancellationToken)
    {
        if (command.Service != _service)
        {
            throw new InvalidOperationException($"{command.Name} belongs to {command.Service}, not {_service}.");
        }
        var parameters = new JsonObject();
        foreach (var (key, value) in command.Parameters)
        {
            parameters[key] = value switch
            {
                null => null,
                PsSwitch sw => new JsonObject { ["$switch"] = sw.IsPresent },
                string[] arr => new JsonArray(arr.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                _ => JsonSerializer.SerializeToNode(value),
            };
        }
        var sw2 = Stopwatch.StartNew();
        var response = await SendAsync("invoke", new JsonObject { ["command"] = command.Name, ["parameters"] = parameters }, _options.CommandTimeout, cancellationToken);
        var objects = new List<JsonObject>();
        if (response["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                objects.Add(item switch
                {
                    JsonObject obj => obj,
                    null => new JsonObject(),
                    _ => new JsonObject { ["Value"] = item.DeepClone() },
                });
            }
        }
        var warnings = response["warnings"] is JsonArray w ? w.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToList() : [];
        return new CommandResult(objects, warnings, sw2.Elapsed);
    }

    /// <summary>Test hook: sends a request that bypasses the .NET catalog so the worker's own allowlist and nonce checks can be verified.</summary>
    internal Task<JsonObject> InvokeUncheckedAsync(string command, JsonObject parameters, string? nonce = null) =>
        SendAsync("invoke", new JsonObject { ["command"] = command, ["parameters"] = parameters }, _options.CommandTimeout, CancellationToken.None, nonce);

    /// <summary>Test hook: simulates a crashed worker process.</summary>
    internal void KillForTests() => _process?.Kill(entireProcessTree: true);

    private async Task<JsonObject> SendAsync(string op, JsonObject payload, TimeSpan timeout, CancellationToken cancellationToken, string? nonceOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _single.WaitAsync(cancellationToken);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            if (!IsAlive)
            {
                throw new RoomOpsException(new DomainError("worker.exited", Text.L($"The {_service} session process is not running. Reconnect on the Connections page.", $"Het sessieproces voor {_service} is niet actief. Verbind opnieuw op de pagina Verbindingen."), ErrorKind.Authentication));
            }
            payload["id"] = id;
            payload["op"] = op;
            payload["nonce"] = nonceOverride ?? _nonce;
            await _process!.StandardInput.WriteLineAsync(payload.ToJsonString().AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);

            JsonObject response;
            try
            {
                // Callers' cancellation (e.g. a UI navigation) does not abort a command already sent to Microsoft 365; only the timeout does.
                response = await tcs.Task.WaitAsync(timeout, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("{Service} worker request {Op} timed out after {Timeout}; terminating worker", _service, op, timeout);
                await DisposeAsync();
                throw new CommandFailedException(op, ErrorKind.Transient, Text.L($"The {_service} request timed out after {timeout.TotalMinutes:0} minute(s). The session was closed; reconnect to continue.", $"Het verzoek aan {_service} is na {timeout.TotalMinutes:0} minuut/minuten verlopen. De sessie is gesloten; verbind opnieuw om door te gaan."));
            }

            if (response["ok"]?.GetValue<bool>() == true) return response;
            var error = response["error"] as JsonObject;
            var message = Sanitize(error?["message"]?.ToString() ?? Text.L("Unknown error", "Onbekende fout"));
            var errorId = error?["errorId"]?.ToString();
            var category = error?["category"]?.ToString();
            var commandName = payload["command"]?.ToString() ?? op;
            var kind = op == "connect" && ErrorClassifier.Classify(message, errorId, category) == ErrorKind.Internal
                ? ErrorKind.Authentication
                : ErrorClassifier.Classify(message, errorId, category);
            if (message.Contains("is not allowed", StringComparison.Ordinal) || message.Contains("not authenticated", StringComparison.Ordinal))
            {
                kind = ErrorKind.PermissionDenied;
            }
            throw new CommandFailedException(commandName, kind, message, errorId);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _single.Release();
        }
    }

    private async Task ReadStdoutAsync()
    {
        var reader = _process!.StandardOutput;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                var index = line.IndexOf(_prefix, StringComparison.Ordinal);
                if (index < 0)
                {
                    if (line.Length > 0)
                    {
                        var clean = Sanitize(line);
                        _logger.LogDebug("[{Service} worker] {Line}", _service, clean);
                        _diagnosticSink?.Invoke(clean);
                    }
                    continue;
                }
                JsonObject frame;
                try
                {
                    frame = JsonNode.Parse(line[(index + _prefix.Length)..]) as JsonObject ?? throw new JsonException("Frame is not an object.");
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Discarding malformed frame from {Service} worker: {Message}", _service, ex.Message);
                    continue;
                }
                if (frame["type"]?.ToString() == "ready")
                {
                    _ready.TrySetResult(frame);
                    continue;
                }
                if (frame["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id) && _pending.TryGetValue(id, out var tcs))
                {
                    tcs.TrySetResult(frame);
                }
                else if (frame["ok"]?.GetValue<bool>() == false && frame["id"] is null)
                {
                    _logger.LogWarning("{Service} worker reported an unrouted error: {Message}", _service, Sanitize(frame["error"]?["message"]?.ToString() ?? string.Empty));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process ended.
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _process!.StandardError.ReadLineAsync()) is not null)
            {
                if (line.Length > 0) _logger.LogDebug("[{Service} worker stderr] {Line}", _service, Sanitize(line));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void OnExited()
    {
        var code = 0;
        try { code = _process?.ExitCode ?? 0; } catch (InvalidOperationException) { }
        var message = Text.L($"The {_service} session process exited (code {code}).", $"Het sessieproces voor {_service} is gestopt (code {code}).");
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new RoomOpsException(new DomainError("worker.exited", message + Text.L(" Reconnect on the Connections page.", " Verbind opnieuw op de pagina Verbindingen."), ErrorKind.Authentication)));
        }
        _ready.TrySetException(new RoomOpsException(new DomainError("worker.exited", message, ErrorKind.Internal)));
        if (!_disposed)
        {
            _logger.LogWarning("{Message}", message);
            Exited?.Invoke(message);
        }
    }

    /// <summary>Removes anything that looks like a bearer token or JWT before text reaches logs or the UI.</summary>
    public static string Sanitize(string text) => TokenPattern().Replace(text, "[redacted]");

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}|(?i:bearer\s+)[A-Za-z0-9._\-]{20,}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?<uri>https://(?:microsoft\.com/devicelogin|login\.microsoft\.com/device)\S*).*?code\s+(?<code>[A-Z0-9-]{6,12})", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceCodePattern();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    var shutdown = new JsonObject { ["id"] = -1, ["op"] = "shutdown", ["nonce"] = _nonce }.ToJsonString();
                    await process.StandardInput.WriteLineAsync(shutdown);
                    await process.StandardInput.FlushAsync();
                    process.StandardInput.Close();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                {
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            // _single is intentionally not disposed: a timed-out request disposes the worker while still holding it.
            process.Dispose();
            _logger.LogInformation("{Service} worker stopped; privileged session state cleared", _service);
        }
    }
}
