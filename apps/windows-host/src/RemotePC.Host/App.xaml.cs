using System.IO;
using System.Threading;
using System.Windows;
using RemotePC.Host.Services;
using RemotePC.Host.ViewModels;

namespace RemotePC.Host;

public partial class App : Application
{
    private Mutex? _single;
    public static HostRuntime Runtime { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One host per Windows user (two hosts would fight over ports and the identity).
        _single = new Mutex(true, @"Local\RemotePC-AI-Host", out var first);
        if (!first)
        {
            MessageBox.Show("RemotePC AI is already running.", "RemotePC AI", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            Runtime?.Log.Write("error", $"UI exception: {ex.Exception.GetType().Name}: {ex.Exception.Message}");
            MessageBox.Show(ex.Exception.Message, "RemotePC AI — error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(DataPaths.Resolve(), "crash.log"), $"{DateTime.Now:O} {ex.ExceptionObject}\n");
            }
            catch (IOException) { }
        };

        Runtime = new HostRuntime();
        ThemeManager.Apply(Runtime.Config.Theme, Runtime.Config.ReducedMotion);
        Loc.Instance.Language = Runtime.Config.Language;
        var window = new MainWindow(new MainViewModel(Runtime), Runtime);
        MainWindow = window;
        if (e.Args.Contains("--minimized")) window.WindowState = WindowState.Minimized;
        window.Show();
        await Runtime.StartAsync();
        if (e.Args.Contains("--smoke-test"))
        {
            // CI/packaging check: the app started, created its data folder and identity, then exits cleanly.
            Runtime.Log.Write("app", "smoke test OK");
            await Task.Delay(1500);
            Shutdown(0);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (Runtime is not null) await Runtime.DisposeAsync();
        _single?.Dispose();
        base.OnExit(e);
    }
}
