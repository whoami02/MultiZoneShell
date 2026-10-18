using System.IO;
using System.Windows;
using MultiZoneShell.Infrastructure;
using MultiZoneShell.Zones;

namespace MultiZoneShell;

public partial class App : Application
{
    public static string ConfigPath { get; private set; } = string.Empty;

    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "MultiZoneShell.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        ConfigPath = e.Args.Length > 0
            ? Path.GetFullPath(e.Args[0])
            : Path.Combine(AppContext.BaseDirectory, "zones.json");

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("Unhandled UI exception", a.Exception);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error("Unobserved task exception", a.Exception);
            a.SetObserved();
        };

        LibVLCSharp.Shared.Core.Initialize();
        Log.Info($"Shell starting. Config: {ConfigPath}");

        base.OnStartup(e);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        VideoZone.DisposeShared();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Log.Info("Shell exited.");
        base.OnExit(e);
    }
}
