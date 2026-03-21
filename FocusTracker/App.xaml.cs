using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using FocusTracker.Data;
using FocusTracker.Helpers;
using FocusTracker.Plugins;
using FocusTracker.Services;
using FocusTracker.Settings;
using Application = System.Windows.Application;

namespace FocusTracker;

public partial class App : Application
{
    public static DatabaseService Database { get; private set; } = null!;
    public static TrackingService Tracker  { get; private set; } = null!;
    public static NotifyIcon?     TrayIcon { get; private set; }
    public static AppSettings     Settings => AppSettings.Instance;

    private static System.Threading.Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Global\\FocusTracker_SingleInstance_Mutex";
        _mutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Already running — if we were launched with a focustracker:// URI,
            // forward it to the running instance via named pipe; otherwise just
            // bring the existing window to front.
            var launchArgs = Environment.GetCommandLineArgs();
            var installArgs = UriSchemeHandler.ParseInstallUri(launchArgs);
            if (installArgs != null)
                UriSchemeHandler.TrySendUri(installArgs.RawUri);
            else
                UriSchemeHandler.TrySendUri("focustracker://show");

            _mutex.Dispose();
            Shutdown();
            return;
        }

        // Global crash logger — escribe en %APPDATA%\FocusTracker\crash.log
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogCrash(ex.ExceptionObject?.ToString() ?? "unknown");
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception?.ToString() ?? "unknown dispatcher error");
            // NO marcar Handled=true — dejar que el error sea visible en desarrollo
        };

        base.OnStartup(e);

        try
        {
            // Load settings first so we know the custom data folder
            _ = AppSettings.Instance;

            Database = new DatabaseService(AppSettings.Instance.DataFolder);
            Tracker  = new TrackingService(Database);

            // ── Plugin system ───────────────────────────────────────────
            // Give the registry access to host services, then let the
            // manager scan and load every enabled .focusplugin from disk.
            PluginRegistry.Instance.SetHost(Database, Tracker);
            PluginManager.Instance.LoadAll();

            // ── URI scheme + IPC ────────────────────────────────────────
            // Register focustracker:// in HKCU so the web store can open
            // the app directly. Then start the named-pipe server so that
            // subsequent launches (from URI clicks while app is running)
            // can forward their install command here.
            UriSchemeHandler.RegisterScheme();
            UriSchemeHandler.StartPipeServer(async uriStr =>
            {
                if (uriStr == "focustracker://show")
                {
                    ShowMainWindow();
                    return;
                }
                var install = UriSchemeHandler.ParseInstallUri([uriStr]);
                if (install != null)
                    await UriSchemeHandler.HandleInstallAsync(install);
            });

            InitTrayIcon();

            var win = new Views.MainWindow();
            MainWindow = win;
            win.Show();

            // ── Handle install URI passed on this launch ─────────────────
            // Defer until after the window is fully rendered.
            var startupInstall = UriSchemeHandler.ParseInstallUri(
                Environment.GetCommandLineArgs());
            if (startupInstall != null)
            {
                win.Dispatcher.BeginInvoke(
                    async () => await UriSchemeHandler.HandleInstallAsync(startupInstall),
                    DispatcherPriority.Loaded);
            }

        }
        catch (Exception ex)
        {
            LogCrash(ex.ToString());
            System.Windows.MessageBox.Show(
                $"Error al iniciar Focus Tracker:\n\n{ex.Message}\n\nRevisá el archivo crash.log en %APPDATA%\\FocusTracker\\",
                "Focus Tracker — Error de inicio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void LogCrash(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FocusTracker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{msg}\n");
        }
        catch { }
    }

    private static Icon LoadTrayIcon()
    {
        // Intentar cargar desde recurso embebido
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/icon.ico");
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null) return new Icon(stream);
        }
        catch { }

        // Fallback: ícono del sistema
        return SystemIcons.Application;
    }

    private void InitTrayIcon()
    {
        TrayIcon = new NotifyIcon
        {
            Text    = "Focus Tracker",
            Icon    = LoadTrayIcon(),
            Visible = true
        };

        // We use our own custom WPF popup instead of the WinForms ContextMenuStrip.
        // Left-click / double-click → restore the window.
        // Right-click → open the custom popup with project shortcuts.
        TrayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowMainWindow();
            else if (e.Button == MouseButtons.Right)
                Current?.Dispatcher.BeginInvoke(ShowTrayPopup, DispatcherPriority.Normal);
        };

        // Double-click always restores (fires after the first MouseClick)
        TrayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    // ── Custom tray context popup ─────────────────────────────────────────
    private static Views.TrayPopup? _trayPopup;

    private static void ShowTrayPopup()
    {
        // Close any existing popup first
        _trayPopup?.Close();

        var projects = Database.GetAllProjects();
        _trayPopup = new Views.TrayPopup(projects);

        // Show offscreen first so UpdateLayout can measure actual size
        _trayPopup.Left = -9999;
        _trayPopup.Top  = -9999;
        _trayPopup.Show();
        _trayPopup.PositionNearCursor();
    }

    public static void ShowMainWindow()
    {
        // NotifyIcon events can fire on a WinForms / OS thread that is NOT the
        // WPF dispatcher thread.  Any WPF call (Show, Activate, WindowState…)
        // made from the wrong thread throws a cross-thread InvalidOperationException
        // that is silently swallowed — resulting in nothing happening at all.
        // BeginInvoke guarantees execution on the WPF UI thread.  We don't need
        // Windows' brief "foreground rights" window here because we're calling
        // Show() on a fully hidden window; the OS treats that as a fresh window
        // creation and gives it focus automatically.
        Current?.Dispatcher.BeginInvoke(DoShowMainWindow, DispatcherPriority.Normal);
    }

    private static void DoShowMainWindow()
    {
        var win = Current?.MainWindow;
        if (win == null) return;

        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;

        if (!win.IsVisible)
            win.Show();

        // Win32 + WPF belt-and-suspenders to ensure the window comes to front
        var hwnd = new WindowInteropHelper(win).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WinApi.ShowWindow(hwnd, WinApi.SW_RESTORE);
            WinApi.SetForegroundWindow(hwnd);
        }

        win.Activate();
        win.Focus();
    }

    public static void RealExit()
    {
        Tracker?.StopTracking();
        PluginRegistry.Instance.ShutdownAll(); // graceful plugin shutdown before DB closes
        UriSchemeHandler.StopPipeServer();
        Tracker?.Dispose();
        Database?.Dispose();
        TrayIcon?.Dispose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PluginRegistry.Instance.ShutdownAll();
        UriSchemeHandler.StopPipeServer();
        TrayIcon?.Dispose();
        Tracker?.Dispose();
        Database?.Dispose();
        base.OnExit(e);
    }
}
