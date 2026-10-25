// RoomOps Studio development host.
//
// Serves the built frontend and the bridge over a WebSocket on 127.0.0.1 so the UI can be developed and tested in a browser
// (including Playwright) on any platform. It is not the product shell: the desktop application uses WebView2 with an in-process
// message bridge and no network listener at all.
//
// Safeguards: loopback binding only, a random per-run token required on the WebSocket, an Origin allowlist, and live Microsoft 365
// connections disabled unless --allow-live is passed explicitly.

using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;
using RoomOps.Bridge;
using RoomOps.Hosting;
using RoomOps.Infrastructure.Persistence;

var port = 5199;
string? staticRoot = null;
var allowLive = false;
var origins = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--static": staticRoot = Path.GetFullPath(args[++i]); break;
        case "--allow-live": allowLive = true; break;
        case "--dev-origin": origins.Add(args[++i].TrimEnd('/')); break;
    }
}
origins.Add($"http://127.0.0.1:{port}");
origins.Add($"http://localhost:{port}");

var token = Environment.GetEnvironmentVariable("ROOMOPS_DEV_TOKEN") is { Length: >= 16 } fixedToken
    ? fixedToken
    : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var paths = new AppPaths();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
builder.Logging.ClearProviders();
var app = builder.Build();

var host = new DevHostServices(paths.Root, allowLive);
using var services = RoomOpsHost.Build(paths, _ => host, consoleLogging: true);
var dispatcher = services.GetRequiredService<BridgeDispatcher>();
var events = services.GetRequiredService<BridgeEventSink>();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (context, next) =>
{
    // Same hardening headers the desktop shell applies through WebView2 navigation rules.
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

app.Map("/bridge", async context =>
{
    var origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
    if (!context.WebSockets.IsWebSocketRequest || !origins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Query["token"].ToString()), Encoding.UTF8.GetBytes(token)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var sendLock = new SemaphoreSlim(1, 1);
    async Task SendAsync(string text)
    {
        await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }
    using var subscription = events.Subscribe(message => _ = SendAsync(message));
    var buffer = new byte[64 * 1024];
    while (socket.State == WebSocketState.Open)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > BridgeDispatcher.MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                return;
            }
        } while (!result.EndOfMessage);
        if (result.MessageType == WebSocketMessageType.Close) break;
        var message = Encoding.UTF8.GetString(stream.ToArray());
        _ = Task.Run(async () => await SendAsync(await dispatcher.DispatchAsync(message, CancellationToken.None)));
    }
    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
    {
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
});

if (staticRoot is not null && !Directory.Exists(staticRoot))
{
    Console.WriteLine($"Static directory not found: {staticRoot}. Only the bridge endpoint is served.");
}
if (staticRoot is not null && Directory.Exists(staticRoot))
{
    var files = new PhysicalFileProvider(staticRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(Path.Combine(staticRoot, "index.html"));
    });
}

Console.WriteLine($"RoomOps Studio dev host listening on http://127.0.0.1:{port}");
Console.WriteLine($"Open: http://127.0.0.1:{port}/?devtoken={token}");
Console.WriteLine(allowLive ? "Live Microsoft 365 connections: ENABLED (--allow-live)" : "Live Microsoft 365 connections: disabled (demo mode only)");
await app.RunAsync();

internal sealed class DevHostServices(string dataDirectory, bool allowLive) : IHostServices
{
    public AppInfo Info { get; } = new("RoomOps Studio", RoomOpsHost.Version, "devhost", RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
        new HostCapabilities(NativeFileDialogs: false, NativeTitleBar: false), dataDirectory, allowLive);

    public Task<SaveFileResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken) => Task.FromResult(new SaveFileResult(false, null, true));

    public Task<OpenFileResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken) => Task.FromResult(new OpenFileResult(false, null, null, true));

    public void Ready()
    {
    }

    public void SetTheme(bool dark)
    {
    }

    public bool OpenExternal(Uri uri) => false;
}
