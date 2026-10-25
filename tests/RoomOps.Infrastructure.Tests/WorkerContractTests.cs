using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RoomOps.Application.Commands;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Common;
using RoomOps.Infrastructure.PowerShell;

namespace RoomOps.Infrastructure.Tests;

/// <summary>
/// Runs the real worker script in a real PowerShell 7 process, with a fake module standing in for the Microsoft modules.
/// Skipped automatically when pwsh is not installed.
/// </summary>
public sealed class WorkerContractTests : IAsyncLifetime
{
    private PowerShellWorker? _worker;
    private readonly List<SignInPrompt> _prompts = [];
    private static readonly string? Pwsh = PowerShellWorker.FindPowerShell(null);

    public async ValueTask InitializeAsync()
    {
        if (Pwsh is null) return;
        var options = new WorkerLaunchOptions
        {
            WorkDirectory = Path.Combine(Path.GetTempPath(), "roomops-worker-tests"),
            TestModulePath = Path.Combine(AppContext.BaseDirectory, "TestModules", "FakeServices.psm1"),
            CommandTimeout = TimeSpan.FromSeconds(30),
        };
        _worker = new PowerShellWorker(ServiceKind.Exchange, options, NullLogger.Instance);
        await _worker.StartAsync(CancellationToken.None);
        var info = await _worker.ConnectAsync(new ConnectRequest(ServiceKind.Exchange, "admin@fake.example", SignInMethod.DeviceCode), null, _prompts.Add, CancellationToken.None);
        Assert.Equal("11111111-2222-3333-4444-555555555555", info.TenantId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null) await _worker.DisposeAsync();
    }

    private PowerShellWorker Worker => _worker ?? throw new InvalidOperationException();

    [Fact]
    public void Connect_surfaces_device_code_instructions_from_host_output()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var prompt = Assert.Single(_prompts);
        Assert.Equal("ABCD1234", prompt.UserCode);
        Assert.StartsWith("https://microsoft.com/devicelogin", prompt.VerificationUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Objects_are_normalized_to_primitives_arrays_and_iso_dates()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var result = await Worker.RunAsync(PsCommand.Create("Get-EXOMailbox", ("Identity", "a@fake.example"), ("Properties", new[] { "ResourceCapacity" })), CancellationToken.None);
        var mailbox = Assert.Single(result.Objects);
        Assert.Equal(8, mailbox["ResourceCapacity"]!.GetValue<int>());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", mailbox["ExternalDirectoryObjectId"]!.GetValue<string>());
        Assert.Equal(2, mailbox["EmailAddresses"]!.AsArray().Count);
        Assert.StartsWith("2024-01-02T03:04:05", mailbox["WhenCreated"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(mailbox["IsDirSynced"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Values_are_bound_literally_and_never_evaluated()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        const string hostile = "'; Remove-Item -Recurse C:\\ ; $(Get-SecretData) `n \" $env:PATH";
        var command = PsCommand.Create("Set-CalendarProcessing",
            ("Identity", "o'brien@fake.example"), ("AdditionalResponse", hostile), ("ResourceDelegates", new[] { "a@fake.example", "$(whoami)" }),
            ("AllowConflicts", false), ("BookingWindowInDays", 90));
        var result = await Worker.RunAsync(command, CancellationToken.None);
        var echo = Assert.Single(result.Objects);
        Assert.Equal("o'brien@fake.example", echo["ReceivedIdentity"]!.GetValue<string>());
        Assert.Equal(hostile, echo["ReceivedResponse"]!.GetValue<string>());
        Assert.Equal(["a@fake.example", "$(whoami)"], echo["ReceivedDelegates"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.False(echo["ReceivedAllowConflicts"]!.GetValue<bool>());
        Assert.Equal(90, echo["ReceivedWindow"]!.GetValue<int>());
        Assert.Contains("Fake warning from Set-CalendarProcessing", result.Warnings);
    }

    [Fact]
    public async Task Non_ascii_text_survives_the_round_trip()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        // On Windows the worker's stdout uses the console's OEM code page; frames must not depend on it.
        const string name = "ANTW-02-Meetingroom Entrepôt – Liège ✓";
        var result = await Worker.RunAsync(PsCommand.Create("Set-CalendarProcessing", ("Identity", "a@fake.example"), ("AdditionalResponse", name)), CancellationToken.None);
        Assert.Equal(name, Assert.Single(result.Objects)["ReceivedResponse"]!.GetValue<string>());
    }

    [Fact]
    public async Task Switch_parameters_bind_including_explicit_false()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var created = await Worker.RunAsync(PsCommand.Create("New-DistributionGroup", ("Name", "Rooms"), ("RoomList", new PsSwitch(true))), CancellationToken.None);
        Assert.True(Assert.Single(created.Objects)["IsRoomList"]!.GetValue<bool>());
        // -Confirm:$false suppresses the ShouldProcess prompt of high-impact cmdlets in the non-interactive worker.
        var removed = await Worker.RunAsync(PsCommand.Create("Remove-DistributionGroupMember", ("Identity", "l@fake.example"), ("Member", "r@fake.example"), ("Confirm", new PsSwitch(false))), CancellationToken.None);
        Assert.Equal("r@fake.example", Assert.Single(removed.Objects)["Removed"]!.GetValue<string>());
    }

    [Fact]
    public async Task Service_errors_are_classified()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var notFound = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.RunAsync(PsCommand.Create("Get-EXOMailbox", ("Identity", "missing@fake.example")), CancellationToken.None));
        Assert.Equal(ErrorKind.NotFound, notFound.Kind);
        var throttled = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.RunAsync(PsCommand.Create("Get-EXOMailbox", ("Identity", "busy@fake.example")), CancellationToken.None));
        Assert.True(throttled.IsRetryable);
    }

    [Fact]
    public async Task Worker_enforces_its_own_allowlist()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var notAllowed = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.InvokeUncheckedAsync("Get-SecretData", new JsonObject()));
        Assert.Equal(ErrorKind.PermissionDenied, notAllowed.Kind);
        var badParameter = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.InvokeUncheckedAsync("Get-EXOMailbox", new JsonObject { ["Filter"] = "*" }));
        Assert.Contains("not allowed", badParameter.Message, StringComparison.Ordinal);
        var structured = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.InvokeUncheckedAsync("Get-EXOMailbox", new JsonObject { ["Identity"] = new JsonObject { ["Script"] = "x" } }));
        Assert.Contains("not accepted", structured.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batches_run_in_parallel_runspaces_and_report_each_item()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var commands = new[] { "a@fake.example", "missing@fake.example", "a@fake.example" }
            .Select(id => PsCommand.Create("Get-EXOMailbox", ("Identity", id), ("Properties", new[] { "ResourceCapacity" })))
            .ToList();
        var progress = new List<int>();
        var results = await Worker.RunManyAsync(commands, 3, progress.Add, CancellationToken.None);
        Assert.Equal([1, 2, 3], progress); // one progress frame per completed item, while the batch runs
        Assert.Equal(3, results.Count);
        Assert.Equal(8, Assert.Single(results[0].Result!.Objects)["ResourceCapacity"]!.GetValue<int>());
        Assert.Equal(ErrorKind.NotFound, results[1].Error!.Kind); // one failing item doesn't fail the batch
        Assert.Equal("Room A", Assert.Single(results[2].Result!.Objects)["DisplayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Batches_are_limited_to_allowlisted_reads()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var write = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.InvokeManyUncheckedAsync("Set-CalendarProcessing", [new JsonObject { ["Identity"] = "a@fake.example" }]));
        Assert.Equal(ErrorKind.PermissionDenied, write.Kind);
        var badParameter = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.InvokeManyUncheckedAsync("Get-EXOMailbox", [new JsonObject { ["Identity"] = "a@fake.example" }, new JsonObject { ["Filter"] = "*" }]));
        Assert.Contains("not allowed", badParameter.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_without_the_session_nonce_are_rejected()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var rejected = await Assert.ThrowsAsync<CommandFailedException>(() => Worker.InvokeUncheckedAsync("Get-EXOMailbox", new JsonObject { ["Identity"] = "a@fake.example" }, nonce: "forged"));
        Assert.Equal(ErrorKind.PermissionDenied, rejected.Kind);
    }

    [Fact]
    public async Task A_crashed_worker_fails_requests_and_reports_it_is_not_alive()
    {
        Assert.SkipWhen(Pwsh is null, "PowerShell 7 is not installed.");
        var exited = new TaskCompletionSource<string>();
        Worker.Exited += m => exited.TrySetResult(m);
        Worker.KillForTests();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Worker.IsAlive);
        var error = await Assert.ThrowsAsync<RoomOpsException>(() => Worker.RunAsync(PsCommand.Create("Get-EXOMailbox", ("Identity", "a@fake.example")), CancellationToken.None));
        Assert.Equal(ErrorKind.Authentication, error.Error.Kind);
    }

    [Fact]
    public void Worker_allowlist_matches_the_command_catalog()
    {
        var (path, _) = PowerShellWorker.MaterializeScript(Path.Combine(Path.GetTempPath(), "roomops-worker-tests"));
        var script = File.ReadAllText(path);
        var calendarParameters = Regex.Match(script, @"\$CalendarProcessingParameters = @\((?<list>[^)]*)\)", RegexOptions.Singleline).Groups["list"].Value;
        var parsed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(script, @"'(?<cmd>[A-Za-z]+-[A-Za-z0-9]+)'\s*=\s*(?<value>@\((?<params>[^)]*)\)|\$CalendarProcessingParameters)", RegexOptions.Singleline))
        {
            var list = m.Groups["value"].Value.StartsWith('$') ? calendarParameters : m.Groups["params"].Value;
            parsed[m.Groups["cmd"].Value] = Regex.Matches(list, "'([^']+)'").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        Assert.Equal(CommandCatalog.All.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase), parsed.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var spec in CommandCatalog.All)
        {
            Assert.True(spec.Parameters.SetEquals(parsed[spec.Name]), $"Parameter mismatch for {spec.Name}");
        }
    }

    [Theory]
    [InlineData("/usr/bin/pwsh", true)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData("/tmp/pwsh-evil", false)]
    public void Only_powershell_7_can_be_configured_as_the_worker_host(string path, bool allowed) =>
        Assert.Equal(allowed, PowerShellWorker.IsPowerShellExecutable(path));

    [Fact]
    public void Token_like_text_is_redacted()
    {
        var text = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789 and eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXZhbHVl";
        var clean = PowerShellWorker.Sanitize(text);
        Assert.DoesNotContain("eyJ", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", clean, StringComparison.Ordinal);
    }
}
