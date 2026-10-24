using System.IO;
using System.Windows;
using System.Security.Principal;
using AudioRouter.Core;

namespace AudioRouter.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    private EventWaitHandle? restoreSignal, exitSignal;
    private RegisteredWaitHandle? restoreWait, exitWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteError(args.Exception);
            if (MainWindow is MainWindow { SmokeTestOutput: not null })
            {
                args.Handled = true;
                Shutdown(1);
                return;
            }
            MessageBox.Show("Audio Router encountered an error and will close. Reopen it to resume routing.\n\n" + args.Exception.Message,
                "Audio Router", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Environment.ExitCode = 1;
            if (MainWindow is MainWindow window) window.RequestExit(); else Shutdown(1);
        };
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            MessageBox.Show("Audio Router requires Windows 11 version 22H2 or later.", "Audio Router");
            Shutdown(1);
            return;
        }
        bool smoke = e.Args.Length == 2 && e.Args[0] is "--smoke-test" or "--device-switch-smoke-test";
        if (!smoke)
        {
            string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            string prefix = @"Local\AudioRouter-" + identity;
            instanceMutex = new Mutex(true, prefix, out ownsMutex);
            restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Restore");
            exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-Exit");
            if (!ownsMutex)
            {
                if (e.Args.Contains("--exit")) exitSignal.Set(); else restoreSignal.Set();
                Shutdown();
                return;
            }
            if (e.Args.Contains("--exit")) { Shutdown(); return; }
        }
        var main = new MainWindow();
        MainWindow = main;
        if (smoke)
        {
            main.SmokeTestOutput = Path.GetFullPath(e.Args[1]);
            main.RunDeviceSwitchSmokeTest = e.Args[0] == "--device-switch-smoke-test";
        }
        else
        {
            restoreWait = ThreadPool.RegisterWaitForSingleObject(restoreSignal!, (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(main.RestoreFromTray);
            }, null, Timeout.Infinite, false);
            exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal!, (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(main.RequestExit);
            }, null, Timeout.Infinite, false);
        }
        SessionEnding += (_, _) => main.RequestExit();
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.DisposeTrayIcon();
        restoreWait?.Unregister(null); exitWait?.Unregister(null);
        restoreSignal?.Dispose(); exitSignal?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static void WriteError(Exception error)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            string path = Path.Combine(SettingsStore.Folder, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
