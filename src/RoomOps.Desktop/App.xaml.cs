using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using RoomOps.Bridge;
using RoomOps.Hosting;
using RoomOps.Infrastructure.Persistence;
using RoomOps.Infrastructure.PowerShell;

namespace RoomOps.Desktop;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan WebViewStartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InterfaceReadyTimeout = TimeSpan.FromSeconds(20);

    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private Mutex? _singleInstance;
    private AppPaths? _paths;
    private ServiceProvider? _services;
    private ILogger? _log;
    private SplashWindow? _splash;
    private MainWindow? _main;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\RoomOpsStudio.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("RoomOps Studio is already running.", "RoomOps Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _splash = new SplashWindow();
        _splash.Show();
        DispatcherUnhandledException += (_, args) =>
        {
            _services?.GetService<ILoggerFactory>()?.CreateLogger("RoomOps").LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show($"An unexpected error occurred. Details were written to the RoomOps log.\n\n{args.Exception.Message}", "RoomOps Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            _splash.SetStatus("Preparing local data…");
            var paths = _paths = new AppPaths();
            var host = new DesktopHostServices(paths.Root, OnFrontendReady, dark => _main?.ApplyTitleBarTheme(dark));
            _services = await Task.Run(() => RoomOpsHost.Build(paths, _ => host));
            _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("RoomOps.Startup");
            host.Attach(() => _main);

            _splash.SetStatus("Starting the interface…");
            // Throws WebView2RuntimeNotFoundException, before the main window opens, when the runtime is missing.
            _log.LogInformation("WebView2 Runtime {Version}", CoreWebView2Environment.GetAvailableBrowserVersionString());
            _main = new MainWindow(_services);
            MainWindow = _main;

            // The WebView2 control only finishes initializing once it is part of a window on screen, so the main window is
            // shown first. The splash stays above it until the interface reports that it has rendered.
            _main.Show();
            _main.ApplyTitleBarTheme(dark: true);
            _services.GetRequiredService<SignInWindow>().Handle = new WindowInteropHelper(_main).Handle;
            _splash.Owner = _main;
            _splash.ShowInTaskbar = false;
            _splash.Activate();
            _ = Task.Delay(InterfaceReadyTimeout).ContinueWith(_ => Dispatcher.Invoke(OnInterfaceReadyTimeout), TaskScheduler.Default);

            try
            {
                await _main.InitializeWebViewAsync().WaitAsync(WebViewStartTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"The Microsoft Edge WebView2 Runtime did not start within {WebViewStartTimeout.TotalSeconds:0} seconds.");
            }
            _log.LogInformation("WebView2 started after {Elapsed} ms; loading the interface", _startup.ElapsedMilliseconds);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            CloseSplash();
            _main?.Hide();
            var answer = MessageBox.Show("RoomOps Studio needs the Microsoft Edge WebView2 Runtime, which is not installed.\n\nOpen the Microsoft download page now?", "WebView2 Runtime required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            }
            Shutdown(1);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Startup failed");
            CloseSplash();
            _main?.Hide();
            var logs = _paths is null ? "" : $"\n\nDetails are in the log folder:\n{_paths.Logs}";
            MessageBox.Show($"RoomOps Studio could not start.\n\n{ex.Message}{logs}", "RoomOps Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Called when the interface reports that it has rendered: the splash gives way to the main window.</summary>
    private void OnFrontendReady()
    {
        if (_splash is null) return;
        _log?.LogInformation("Interface ready after {Elapsed} ms", _startup.ElapsedMilliseconds);
        CloseSplash();
        _main?.Activate();
    }

    private void OnInterfaceReadyTimeout()
    {
        if (_splash is null || _main is null) return;
        _log?.LogWarning("The interface did not report ready within {Seconds} s; showing the main window anyway", InterfaceReadyTimeout.TotalSeconds);
        CloseSplash();
        _main.Activate();
    }

    private void CloseSplash()
    {
        _splash?.Close();
        _splash = null;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            // Disposing the session manager closes every PowerShell worker, clearing privileged session state.
            await _services.DisposeAsync();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Desktop implementations of host capabilities: native file dialogs, window theming and external links.</summary>
internal sealed class DesktopHostServices(string dataDirectory, Action ready, Action<bool> setTheme) : IHostServices
{
    private Func<Window?> _owner = () => null;

    public void Attach(Func<Window?> owner) => _owner = owner;

    public AppInfo Info { get; } = new("RoomOps Studio", RoomOpsHost.Version, "desktop", RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
        new HostCapabilities(NativeFileDialogs: true, NativeTitleBar: true), dataDirectory, LiveConnectionsAllowed: true);

    private static string FilterFor(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "html" => "HTML report (*.html)|*.html",
            "json" => "JSON (*.json)|*.json",
            "csv" => "CSV (*.csv)|*.csv",
            _ => "All files (*.*)|*.*",
        };
    }

    public Task<SaveFileResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken) =>
        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            var dialog = new SaveFileDialog { FileName = Path.GetFileName(request.FileName), Filter = FilterFor(request.FileName), AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(_owner()) != true) return new SaveFileResult(false, null, false);
            await File.WriteAllTextAsync(dialog.FileName, request.Content, cancellationToken);
            return new SaveFileResult(true, dialog.FileName, false);
        });

    public Task<OpenFileResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken) =>
        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            var patterns = string.Join(";", request.Extensions.Select(x => $"*.{x.Trim('.').ToLowerInvariant()}"));
            var dialog = new OpenFileDialog { Filter = $"Supported files ({patterns})|{patterns}", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(_owner()) != true) return new OpenFileResult(false, null, null, false);
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 10 * 1024 * 1024) throw RoomOps.Domain.Common.RoomOpsException.Validation("files.tooLarge", "The file is larger than 10 MB.");
            return new OpenFileResult(true, info.Name, await File.ReadAllTextAsync(dialog.FileName, cancellationToken), false);
        });

    public void Ready() => System.Windows.Application.Current.Dispatcher.Invoke(ready);

    public void SetTheme(bool dark) => System.Windows.Application.Current.Dispatcher.Invoke(() => setTheme(dark));

    public bool OpenExternal(Uri uri)
    {
        // The bridge has already restricted this to https Microsoft documentation and admin portals.
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        return true;
    }
}
