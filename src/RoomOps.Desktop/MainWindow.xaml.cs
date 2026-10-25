using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using RoomOps.Bridge;
using RoomOps.Infrastructure.Persistence;

namespace RoomOps.Desktop;

/// <summary>
/// Hosts the RoomOps frontend in WebView2. The bundle is served from the application folder through a virtual host mapping,
/// so there is no local web server. Only messages from that origin reach the bridge dispatcher; every other navigation is blocked.
/// </summary>
public partial class MainWindow : Window
{
    public const string Host = "app.roomops.example";
    public const string Origin = "https://" + Host + "/";

    private readonly IServiceProvider _services;
    private readonly ILogger<MainWindow> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private IDisposable? _events;
    private bool _interfaceLoaded;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _logger = services.GetRequiredService<ILogger<MainWindow>>();
        Closed += (_, _) =>
        {
            _shutdown.Cancel();
            _events?.Dispose();
        };
    }

    public async Task InitializeWebViewAsync()
    {
        // The control waits for its own child window, which WPF creates only for a window that has been shown.
        // Initializing a hidden window never completes.
        if (!IsVisible) throw new InvalidOperationException("Show the main window before initializing WebView2.");
        var paths = _services.GetRequiredService<AppPaths>();
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            throw new FileNotFoundException("The RoomOps user interface files are missing from the installation.", Path.Combine(webRoot, "index.html"));
        }

        var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false };
        var environment = await CoreWebView2Environment.CreateAsync(null, paths.WebView2, options);
        await WebView.EnsureCoreWebView2Async(environment);
        var core = WebView.CoreWebView2;

        core.SetVirtualHostNameToFolderMapping(Host, webRoot, CoreWebView2HostResourceAccessKind.Deny);

        var settings = core.Settings;
#if DEBUG
        var developer = true;
#else
        var developer = false;
#endif
        settings.AreDevToolsEnabled = developer;
        settings.AreDefaultContextMenusEnabled = developer;
        settings.AreBrowserAcceleratorKeysEnabled = developer;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                _logger.LogWarning("Blocked navigation to {Uri}", e.Uri);
            }
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _logger.LogWarning("Blocked new window for {Uri}", e.Uri);
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) => _logger.LogError("WebView2 process failed: {Kind} {Reason}", e.ProcessFailedKind, e.Reason);
        core.NavigationCompleted += OnNavigationCompleted;

        _events = _services.GetRequiredService<BridgeEventSink>().Subscribe(Post);
        core.Navigate(Origin + "index.html");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            if (!_interfaceLoaded) _logger.LogInformation("Interface page loaded");
            _interfaceLoaded = true;
            return;
        }
        // Blocked navigations end as OperationCanceled. Other failures after the first load are only logged.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
        _logger.LogError("The interface failed to load: {Status} (HTTP {StatusCode})", e.WebErrorStatus, e.HttpStatusCode);
        if (_interfaceLoaded) return;
        MessageBox.Show(this, $"The RoomOps Studio interface could not be loaded ({e.WebErrorStatus}).\n\nDetails are in the log folder:\n{_services.GetRequiredService<AppPaths>().Logs}",
            "RoomOps Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        Close();
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ignored bridge message from unexpected origin {Origin}", e.Source);
            return;
        }
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Ignored a non-string bridge message");
            return;
        }
        var dispatcher = _services.GetRequiredService<BridgeDispatcher>();
        _ = Task.Run(async () => Post(await dispatcher.DispatchAsync(message, _shutdown.Token)));
    }

    private void Post(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString(json);
            }
            catch (InvalidOperationException)
            {
                // The WebView is closing.
            }
        });
    }

    // ------------------------------------------------------------------------------------------------ Title bar theme

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Matches the native title bar to the application theme (Windows 10 20H1+ dark mode, Windows 11 caption color).</summary>
    public void ApplyTitleBarTheme(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
        var caption = dark ? 0x00181412 : 0x00F2F4F4; // COLORREF 0x00BBGGRR: #121418 / #F4F4F2
        _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }
}
