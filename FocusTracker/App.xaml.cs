using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using FocusTracker.Data;
using FocusTracker.Services;
using Application = System.Windows.Application;

namespace FocusTracker;

public partial class App : Application
{
    public static DatabaseService Database { get; private set; } = null!;
    public static TrackingService Tracker  { get; private set; } = null!;
    public static NotifyIcon? TrayIcon     { get; private set; }

    private static System.Threading.Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Global\\FocusTracker_SingleInstance_Mutex";
        _mutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Already running — focus existing window if possible
            // (Note: in a real app we might use IPC to tell the other instance to show up)
            System.Windows.MessageBox.Show("Focus Tracker ya está en ejecución.", "Focus Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
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
            Database = new DatabaseService();
            Tracker  = new TrackingService(Database);
            InitTrayIcon();

            var win = new Views.MainWindow();
            MainWindow = win;
            win.Show();

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

        var menu = new ContextMenuStrip();

        var itemShow = new ToolStripMenuItem("Mostrar ventana");
        itemShow.Font = new System.Drawing.Font(itemShow.Font, System.Drawing.FontStyle.Bold);
        itemShow.Click += (_, _) => ShowMainWindow();

        var itemStop = new ToolStripMenuItem("Detener tracking");
        itemStop.Click += (_, _) =>
        {
            Tracker.StopTracking();
            TrayIcon.Text = "Focus Tracker — inactivo";
        };

        var itemExit = new ToolStripMenuItem("Salir de Focus Tracker");
        itemExit.Click += (_, _) => RealExit();

        menu.Items.Add(itemShow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(itemStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(itemExit);

        TrayIcon.ContextMenuStrip = menu;
        TrayIcon.DoubleClick += (_, _) => ShowMainWindow();

        menu.Opening += (_, _) =>
        {
            itemStop.Enabled = Tracker.IsTracking;
            itemStop.Text    = Tracker.IsTracking ? "Detener tracking" : "Tracking inactivo";
            TrayIcon.Text    = Tracker.IsTracking
                ? $"Focus Tracker — {Tracker.CurrentFocusedApp ?? "esperando…"}"
                : "Focus Tracker — inactivo";
        };
    }

    public static void ShowMainWindow()
    {
        var win = Current.MainWindow;
        if (win == null) return;
        win.Show();
        win.WindowState = System.Windows.WindowState.Normal;
        win.Activate();
    }

    public static void RealExit()
    {
        Tracker?.StopTracking();
        Tracker?.Dispose();
        Database?.Dispose();
        TrayIcon?.Dispose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TrayIcon?.Dispose();
        Tracker?.Dispose();
        Database?.Dispose();
        base.OnExit(e);
    }
}
