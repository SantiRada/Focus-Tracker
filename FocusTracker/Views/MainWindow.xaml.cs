using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FocusTracker.Helpers;
using FocusTracker.Models;
using FocusTracker.Services;
using FocusTracker.Settings;
using FocusTracker.ViewModels;
using WpfColor       = System.Windows.Media.Color;
using WpfBrush       = System.Windows.Media.SolidColorBrush;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfButton      = System.Windows.Controls.Button;
using WpfCheckBox    = System.Windows.Controls.CheckBox;
using WpfTextBox     = System.Windows.Controls.TextBox;
using WpfComboBox    = System.Windows.Controls.ComboBox;
using WpfMessageBox  = System.Windows.MessageBox;

namespace FocusTracker.Views;

public partial class MainWindow : Window
{
    // ── State ─────────────────────────────────────────────────────────────
    private List<InstalledApp>          _allApps      = new();
    private List<TrackedUrl>            _trackedUrls  = new();
    private List<UsageSummaryViewModel> _dashboardData = new();
    private List<Project>               _projects     = new();
    private Project?                    _currentProject;

    private readonly DispatcherTimer _uiTimer;
    private DateTime _sessionStart;
    private bool _trayHintShown = false;
    private string _currentTimeTab    = "Today";
    private string _currentProjTimeTab = "Today";
    private int? _pendingProjectId = null; // selected project for new session
    private string _currentTrackedAppName = "—"; // Track current active app

    private int _sessionPage = 1;
    private const int SESSION_PAGE_SIZE = 20;
    private int _sessionTotalPages = 1;

    // ── App update ────────────────────────────────────────────────────────
    private string? _updateDownloadUrl;

    // ── Plugin store API ──────────────────────────────────────────────────
    private static readonly HttpClient _storeHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private bool   _storeLoaded = false;
    private List<StorePluginDto> _storePluginsData = new();
    private StorePluginDto? _detailPlugin;   // currently shown in detail panel
    private StorePluginDto? _modalPlugin;    // currently shown in modal
    private readonly HashSet<string> _installingIds = new();
    private DispatcherTimer? _toastTimer;

    private sealed record StorePluginDto(
        string  Id,
        string  Name,
        string  Author,
        string  Version,
        string  Description,
        string  DownloadUrl,
        int     DownloadCount,
        string  CategoryName,
        string  CategorySlug,
        string  PricingType,    // "free" | "paid" | "donation" | "subscription"
        decimal Price,
        string  Currency);

    private readonly ToggleButton[] _timeTabs = null!;
    private readonly ToggleButton[] _projTimeTabs = null!;

    // ── Startup registry ──────────────────────────────────────────────────
    [DllImport("kernel32.dll")] static extern uint GetModuleFileName(IntPtr h, System.Text.StringBuilder buf, uint size);
    private static string GetExePath()
    {
        var sb = new System.Text.StringBuilder(260);
        GetModuleFileName(IntPtr.Zero, sb, 260);
        return sb.ToString();
    }
    private static void RegisterStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("FocusTracker", $"\"{GetExePath()}\"");
        }
        catch { }
    }
    private static void UnregisterStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("FocusTracker", false);
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        if (App.Settings.StartWithWindows) RegisterStartup(); else UnregisterStartup();

        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        TxtAppVersion.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : $"v{AppConfig.VERSION}";

        _timeTabs     = new[] { TabToday, TabWeek, TabMonth, TabYear, TabCustom };
        _projTimeTabs = new[] { ProjTabToday, ProjTabWeek, ProjTabMonth, ProjTabYear, ProjTabCustom };

        App.Tracker.FocusChanged    += (app, _) => Dispatcher.Invoke(() => TxtCurrentApp.Text = app);
        App.Tracker.AlarmTriggered  += (title, msg) => Dispatcher.Invoke(() => ShowAlarmNotification(title, msg));
        App.Tracker.IdleDetected    += () => Dispatcher.Invoke(OnIdleDetected);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        // ── Plugin cross-layer events ──────────────────────────────────────
        // Rebuild the plugin settings panel when a plugin calls RefreshSettings()
        FocusTracker.Plugins.PluginRegistry.SettingsRefreshRequested += LoadPluginSettingCards;

        // Navigate to a plugin's settings tab when it calls NavigateToPluginSettings()
        FocusTracker.Plugins.PluginRegistry.NavigateToPluginSettingsRequested += tabName =>
        {
            _activeSettingsTab = tabName;
            ShowPage("Settings");
            LoadSettingsPage();
        };

        SourceInitialized += (s, e) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WinApi.EnableDarkTitleBar(hwnd);
        };

        Loaded += async (_, _) =>
        {
            _projects = App.Database.GetAllProjects();
            RefreshHomeRecentProjects();
            if (!OnboardingWindow.IsCompleted()) ShowOnboardingOverlay();
            await System.Threading.Tasks.Task.Run(LoadApps);
            _ = CheckForUpdateAsync();
        };

        ShowPage("Setup");
    }

    private void RefreshHomeRecentProjects()
    {
        var recent = _projects.Take(3).Select(p => new {
            p.Id,
            p.Name,
            TrackedKeysDisplay = p.TrackedKeys.Split(',').Where(k => !k.StartsWith("[opt:")).FirstOrDefault() ?? "Sin aplicaciones",
            OriginalProject = p
        }).ToList();

        RecentProjectsList.ItemsSource = recent;
        HomeNoProjects.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeBaseContent.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnStartRecentProject_Click(object s, RoutedEventArgs e)
    {
        if (s is WpfButton btn && btn.Tag is Project proj)
        {
            StartProjectSession(proj);
        }
    }

    // internal so TrayPopup can call it directly
    internal void StartProjectSession(Project proj)
    {
        if (proj == null) return;
        if (_allApps == null || _allApps.Count == 0)
        {
            ToastWindow.Show("Cargando", "La lista de aplicaciones se está cargando. Intentá de nuevo en 2 segundos.");
            return;
        }

        _currentProject = proj;
        _pendingProjectId = proj.Id;
        
        // Load apps for this project
        var keys = (proj.TrackedKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? k[..^4] : k)
            .ToList();
            
        var optKey = keys.FirstOrDefault(k => k.StartsWith("[opt:"));
        bool unfocus = optKey?.Contains("unfocus=1") == true;
        bool unify   = optKey?.Contains("unify=1") == true;
        keys = keys.Where(k => !k.StartsWith("[opt:")).ToList();

        var apps = _allApps.Where(a => keys.Contains(a.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a.ExecutableName[..^4] : a.ExecutableName, StringComparer.OrdinalIgnoreCase)).ToList();

        // Parse URL keys directly from TrackedKeys (format: "[host]").
        // _trackedUrls is never populated for project-card sessions, so we build
        // the list from the raw keys instead of trying to filter that empty list.
        var urls = keys
            .Where(k => k.StartsWith("[") && k.EndsWith("]") && !k.StartsWith("[opt:"))
            .Select(k => { var h = k[1..^1]; return new TrackedUrl { Host = h, Label = MakeUrlLabel(h), IsSelected = true, TrackUnfocus = unfocus }; })
            .ToList();

        if (unify) apps = UnifyVersions(apps);
        foreach (var a in apps) a.TrackUnfocus = unfocus;

        try 
        {
            App.Tracker.StartTracking(apps, urls, proj.Name, proj.Id,
                                      sessionAlarmMins: proj.SessionTimeAlarmSeconds.HasValue ? proj.SessionTimeAlarmSeconds.Value / 60 : null,
                                      totalAlarmMins: proj.TotalTimeAlarmSeconds.HasValue ? proj.TotalTimeAlarmSeconds.Value / 60 : null);
            PluginRegistry.Instance.NotifyTrackingStarted(proj.Id);
            _sessionStart = DateTime.Now;
            
            // Show Live Dashboard
            TxtLiveProjName.Text = proj.Name;
            HomeBaseHeader.Visibility = Visibility.Collapsed;
            HomeBaseContent.Visibility = Visibility.Collapsed;
            HomeLiveDashboard.Visibility = Visibility.Visible;
            HomeSessionSummary.Visibility = Visibility.Collapsed;
            
            // Show Alarm Indicators in Home Dashboard
            if (proj.SessionTimeAlarmSeconds.HasValue && proj.SessionTimeAlarmSeconds.Value > 0)
            {
                LiveAlarmSession.Visibility = Visibility.Visible;
                TxtLiveAlarmSession.Text = FormatDurationShort(proj.SessionTimeAlarmSeconds.Value);
            }
            else LiveAlarmSession.Visibility = Visibility.Collapsed;

            if (proj.TotalTimeAlarmSeconds.HasValue && proj.TotalTimeAlarmSeconds.Value > 0)
            {
                LiveAlarmTotal.Visibility = Visibility.Visible;
                TxtLiveAlarmTotal.Text = FormatDurationShort(proj.TotalTimeAlarmSeconds.Value);
            }
            else LiveAlarmTotal.Visibility = Visibility.Collapsed;

            BtnSidebarStop.IsEnabled = true;
            StatusDot.Fill = (WpfBrush)FindResource("AccentBrush");
            TxtStatusLabel.Text = "TRACKING";
            
            // Initial refresh of stats (should be empty for new session)
            RefreshLiveDashboardStats();
        }
        catch (Exception ex)
        {
            ToastWindow.Show("Error", "No se pudo iniciar la sesión: " + ex.Message);
        }
    }

    private void BtnSummaryBack_Click(object s, RoutedEventArgs e)
    {
        HomeBaseHeader.Visibility = Visibility.Visible;
        HomeBaseContent.Visibility = Visibility.Visible;
        HomeLiveDashboard.Visibility = Visibility.Collapsed;
        HomeSessionSummary.Visibility = Visibility.Collapsed;
        RefreshHomeRecentProjects();
    }

    private void ShowAlarmNotification(string title, string message)
    {
        // Notification should show even if app is in background
        ToastWindow.Show(title, message, ToastKind.Info);

        // Also show a system tray notification if possible
        if (App.TrayIcon != null)
        {
            App.TrayIcon.ShowBalloonTip(3000, title, message, System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    // ── App loading ───────────────────────────────────────────────────────
    private void LoadApps()
    {
        _allApps = InstalledAppsHelper.GetInstalledApps();
    }

    private void NavSetup_Click(object s, RoutedEventArgs e)
    {
        ShowPage("Setup");
        LoadHomePluginContent();
    }

    private void GoToProjects_Click(object s, RoutedEventArgs e) => ShowPage("Projects");

    private void NavDashboard_Click(object s, RoutedEventArgs e) { ShowPage("Dashboard"); LoadDashboard();   LoadScreenPluginButtons(FocusTracker.Plugins.PluginScreenTarget.Dashboard, PluginBtnsDashboard); }
    private void NavSessions_Click(object s, RoutedEventArgs e)  { ShowPage("Sessions");  LoadSessions();    LoadScreenPluginButtons(FocusTracker.Plugins.PluginScreenTarget.Sessions,  PluginBtnsSessions);  }
    private void NavPlugins_Click(object s, RoutedEventArgs e)   { ShowPage("Plugins");   LoadPluginsPage(); }
    private void NavHelp_Click(object s, RoutedEventArgs e)      => ShowPage("Help");
    private void NavSettings_Click(object s, RoutedEventArgs e)  { ShowPage("Settings");  LoadSettingsPage(); }
    private void HelpSection_Toggle(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (s is not FrameworkElement el || el.Tag is not string bodyName) return;
        var body = FindName(bodyName) as System.Windows.Controls.Panel;
        if (body == null) return;
        body.Visibility = body.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowOnboarding_Click(object s, RoutedEventArgs e) => ShowOnboardingOverlay();

    private void NavProjects_Click(object s, RoutedEventArgs e)  { ShowPage("Projects");  LoadProjects();    LoadScreenPluginButtons(FocusTracker.Plugins.PluginScreenTarget.Projects,  PluginBtnsProjects);  }
    private void BackToSessions_Click(object s, RoutedEventArgs e) { ShowPage("Sessions"); LoadSessions(); }

    // ── Plugin content loaders for screens ───────────────────────────────────

    private void LoadHomePluginContent()
    {
        LoadScreenPluginButtons(FocusTracker.Plugins.PluginScreenTarget.Home, PluginBtnsHome);
        PluginHomeContainer.Children.Clear();
        LoadPluginHomeCards(PluginHomeContainer);
    }

    private void LoadScreenPluginButtons(FocusTracker.Plugins.PluginScreenTarget screen,
                                         System.Windows.Controls.Panel container)
    {
        container.Children.Clear();
        var btns = FocusTracker.Plugins.PluginRegistry.Instance.GetButtonsForScreen(screen);
        foreach (var btn in btns)
            container.Children.Add(BuildPluginButton(btn));
        // Apply initial visibility for conditional buttons
        RefreshPluginButtonVisibility(container);
    }

    /// <summary>
    /// Iterates plugin buttons in a panel and updates Visibility for those
    /// that have a <c>GetIsVisible</c> callback. Called from the UI timer.
    /// </summary>
    private static void RefreshPluginButtonVisibility(System.Windows.Controls.Panel container)
    {
        foreach (System.Windows.UIElement elem in container.Children)
        {
            if (elem is WpfButton btn &&
                btn.Tag is FocusTracker.Plugins.PluginButtonContribution contrib &&
                contrib.GetIsVisible != null)
            {
                try { elem.Visibility = contrib.GetIsVisible() ? Visibility.Visible : Visibility.Collapsed; }
                catch { /* plugin fault — leave current state */ }
            }
        }
    }

    private void ShowPage(string page)
    {
        PageSetup.Visibility         = page == "Setup"          ? Visibility.Visible : Visibility.Collapsed;
        PageDashboard.Visibility     = page == "Dashboard"      ? Visibility.Visible : Visibility.Collapsed;
        PageSessions.Visibility      = page == "Sessions"       ? Visibility.Visible : Visibility.Collapsed;
        PageSessionDetail.Visibility = page == "SessionDetail"  ? Visibility.Visible : Visibility.Collapsed;
        PageProjects.Visibility      = page == "Projects"       ? Visibility.Visible : Visibility.Collapsed;
        PageProjectDetail.Visibility = page == "ProjectDetail"  ? Visibility.Visible : Visibility.Collapsed;
        PageProjectEdit.Visibility   = page == "ProjectEdit"    ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility          = page == "Help"           ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility      = page == "Settings"       ? Visibility.Visible : Visibility.Collapsed;
        PagePlugins.Visibility       = page == "Plugins"        ? Visibility.Visible : Visibility.Collapsed;
        PageLite.Visibility          = page == "Lite"           ? Visibility.Visible : Visibility.Collapsed;
        PageLiteResult.Visibility    = page == "LiteResult"     ? Visibility.Visible : Visibility.Collapsed;

        if (page == "Setup")
        {
            if (App.Tracker.IsTracking)
            {
                HomeBaseHeader.Visibility = Visibility.Collapsed;
                HomeBaseContent.Visibility = Visibility.Collapsed;
                HomeLiveDashboard.Visibility = Visibility.Visible;
                HomeSessionSummary.Visibility = Visibility.Collapsed;
                RefreshLiveDashboardStats();
            }
            else
            {
                // If we're coming back and tracking stopped, show base content
                HomeBaseHeader.Visibility = Visibility.Visible;
                HomeBaseContent.Visibility = Visibility.Visible;
                HomeLiveDashboard.Visibility = Visibility.Collapsed;
                HomeSessionSummary.Visibility = Visibility.Collapsed;
                RefreshHomeRecentProjects();
            }
        }

        BtnNavSetup.Tag      = page == "Setup"                                        ? "active" : "";
        BtnNavDashboard.Tag  = page == "Dashboard"                                    ? "active" : "";
        BtnNavProjects.Tag   = page is "Projects" or "ProjectDetail" or "ProjectEdit" ? "active" : "";
        BtnNavPlugins.Tag    = page == "Plugins"                                      ? "active" : "";
        BtnNavSettings.Tag   = page == "Settings"                                     ? "active" : "";
        BtnNavHelp.Tag       = page == "Help"                                         ? "active" : "";
    }

    private void BtnStop_Click(object s, RoutedEventArgs e)
    {
        var sessionId = App.Tracker.CurrentSessionId;
        App.Tracker.StopTracking();
        PluginRegistry.Instance.NotifyTrackingStopped();
        BtnSidebarStop.IsEnabled = false;
        StatusDot.Fill = (WpfBrush)FindResource("TextMutedBrush");
        TxtStatusLabel.Text = "INACTIVO";
        TxtCurrentApp.Text  = "—";
        TxtSessionTime.Text = "—";

        // If we're on Home page, show summary
        if (PageSetup.Visibility == Visibility.Visible && HomeLiveDashboard.Visibility == Visibility.Visible)
        {
            ShowSessionSummary(sessionId);
        }
        else
        {
            RefreshHomeRecentProjects();
            _pendingProjectId = null;
        }

        // Auto-delete empty sessions (no focus events recorded)
        var detail = App.Database.GetSessionDetail(sessionId);
        if (detail.Count == 0)
            App.Database.DeleteSession(sessionId);

        // Re-enable Comenzar buttons if project list is visible
        if (PageProjects.Visibility == Visibility.Visible) LoadProjects();
    }

    private void ShowSessionSummary(int sessionId)
    {
        HomeBaseHeader.Visibility = Visibility.Collapsed;
        HomeBaseContent.Visibility = Visibility.Collapsed;
        HomeLiveDashboard.Visibility = Visibility.Collapsed;
        HomeSessionSummary.Visibility = Visibility.Visible;

        var duration = DateTime.Now - _sessionStart;
        TxtSummaryTime.Text = $"Duración: {FormatDuration(duration)}";
        
        // Load apps for THIS SESSION only (fix data leak)
        var items = App.Database.GetSessionDetail(sessionId);
        var top = items.OrderByDescending(i => i.FocusSeconds + i.UnfocusSeconds)
                       .Take(5)
                       .Select(i => new { 
                           Name = i.AppDisplayName, 
                           FocusDisplay = FormatDurationShort(i.FocusSeconds),
                           UnfocusDisplay = FormatDurationShort(i.UnfocusSeconds),
                           DurationDisplay = FormatDurationShort(i.FocusSeconds + i.UnfocusSeconds)
                       }).ToList();
        
        SummaryTopApps.ItemsSource = top;
        _pendingProjectId = null;
    }

    private void UpdateLiveUI(string appName, TimeSpan duration)
    {
        TxtCurrentApp.Text = appName;
        _currentTrackedAppName = appName; // Update local state
        
        if (PageSetup.Visibility == Visibility.Visible && HomeLiveDashboard.Visibility == Visibility.Visible)
        {
            // Periodically refresh the live activity list
            RefreshLiveDashboardStats();
        }
    }

    private void RefreshLiveDashboardStats()
    {
        if (_pendingProjectId == null || App.Tracker.CurrentSessionId == 0)
        {
            TxtLiveFocus.Text   = "—";
            TxtLiveTotal.Text   = "0s";
            HomeLiveList.ItemsSource = null;
            return;
        }

        var items = App.Database.GetSessionDetail(App.Tracker.CurrentSessionId);
        string? matchName = App.Tracker.CurrentFocusedApp;
        
        TxtLiveCurrentAppName.Text = matchName ?? "—";
        TxtLiveFocus.Text = matchName ?? "--";  // Card 1 = app name (or -- if untracked)

        var elapsed = DateTime.Now - _sessionStart;
        TxtLiveTotal.Text = FormatDurationShort((long)elapsed.TotalSeconds); // Card 2 = Session Duration Timer

        HomeLiveList.ItemsSource = items.OrderByDescending(i => i.FocusSeconds + i.UnfocusSeconds)
            .Select(i => new {
                Name           = i.AppDisplayName,
                FocusDisplay   = FormatDurationShort(i.FocusSeconds),
                UnfocusDisplay = FormatDurationShort(i.UnfocusSeconds),
                TotalDisplay   = FormatDurationShort(i.FocusSeconds + i.UnfocusSeconds)
            }).ToList();
    }

    private string FormatDurationShort(long seconds)
    {
        var d = TimeSpan.FromSeconds(seconds);
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }

    private string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }

    private void ToggleUnify_Changed(object s, RoutedEventArgs e) { } // logic moved to projects

    private static List<InstalledApp> UnifyVersions(List<InstalledApp> apps)
    {
        var pattern = new Regex(@"[\s\-_v]?\d+(\.\d+)+$", RegexOptions.IgnoreCase);
        var unified = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var baseExe = pattern.Replace(app.ExecutableName, "").Trim().ToLower();
            if (!unified.ContainsKey(baseExe))
                unified[baseExe] = new InstalledApp
                {
                    DisplayName = pattern.Replace(app.DisplayName, "").Trim(),
                    ExecutableName = baseExe, IsSelected = true
                };
        }
        return unified.Values.ToList();
    }

    // ── Last project card ─────────────────────────────────────────────────
    private void RefreshLastProjectCard()
    {
        // Last project card removed from UI — kept as no-op to avoid removing all call sites
        _projects = App.Database.GetAllProjects();
    }

    // ── Dashboard ─────────────────────────────────────────────────────────
    private void TimeTab_Click(object s, RoutedEventArgs e)
    {
        if (s is not ToggleButton btn) return;
        foreach (var t in _timeTabs) t.IsChecked = false;
        btn.IsChecked = true;
        _currentTimeTab = btn.Tag?.ToString() ?? "Today";
        CustomDatePanel.Visibility = _currentTimeTab == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        if (_currentTimeTab != "Custom") LoadDashboard();
    }

    private void DateRange_Changed(object? s, SelectionChangedEventArgs e) => LoadDashboard();

    private void LoadDashboard()
    {
        var (from, to) = GetDateRange(_currentTimeTab, DateFrom, DateTo);
        var summaries  = App.Database.GetUsageSummaries(from, to);
        double total   = summaries.Sum(x => x.FocusSeconds + x.UnfocusSeconds);
        _dashboardData = summaries.Select(x => new UsageSummaryViewModel
        {
            ProcessName    = x.ProcessName,
            AppDisplayName = x.AppDisplayName,
            FocusTime      = x.FocusTime,
            UnfocusTime    = x.UnfocusTime,
            EventCount     = x.EventCount,
            IsUrl          = x.IsUrl,
            TotalPercent   = total > 0 ? (x.FocusSeconds + x.UnfocusSeconds) / total * 100.0 : 0
        }).ToList();

        SummaryCards.ItemsSource = _dashboardData.Take(3).ToList();
        ApplyDashFilter();
        TxtNoDashData.Visibility = _dashboardData.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtDashSearch_TextChanged(object s, TextChangedEventArgs e) => ApplyDashFilter();

    private void ApplyDashFilter()
    {
        var q = TxtDashSearch?.Text?.Trim() ?? "";
        DetailTable.ItemsSource = string.IsNullOrEmpty(q) ? _dashboardData
            : _dashboardData.Where(x => x.AppDisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || x.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void DeleteAppData_Click(object s, RoutedEventArgs e)
    {
        if (s is not WpfButton btn) return;
        var name = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(name)) return;
        if (ConfirmDialog.Show(this, "Eliminar datos", $"¿Eliminar todos los datos de \"{name}\"?\nEsta acción no se puede deshacer."))
        {
            App.Database.DeleteEventsByProcess(name);
            LoadDashboard();
        }
    }

    // ── Sessions ──────────────────────────────────────────────────────────
    private void LoadSessions()
    {
        var sessions = App.Database.GetAllSessions();

        // Apply filters
        var filterProj = TxtSessionFilterProject.Text.Trim();
        var filterDate = DateSessionFilter.SelectedDate;

        if (!string.IsNullOrEmpty(filterProj))
        {
            sessions = sessions.Where(s => (s.DisplayName ?? "").Contains(filterProj, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (filterDate.HasValue)
        {
            sessions = sessions.Where(s => s.StartTime.Date == filterDate.Value.Date).ToList();
        }

        // Pagination
        _sessionTotalPages = (int)Math.Ceiling(sessions.Count / (double)SESSION_PAGE_SIZE);
        if (_sessionTotalPages == 0) _sessionTotalPages = 1;
        if (_sessionPage > _sessionTotalPages) _sessionPage = _sessionTotalPages;

        TxtPageInfo.Text = $"Página {_sessionPage} de {_sessionTotalPages}";
        BtnPrevPage.IsEnabled = _sessionPage > 1;
        BtnNextPage.IsEnabled = _sessionPage < _sessionTotalPages;

        var pagedSessions = sessions
            .Skip((_sessionPage - 1) * SESSION_PAGE_SIZE)
            .Take(SESSION_PAGE_SIZE)
            .ToList();

        var vms = new List<object>();
        DateTime? lastDate = null;

        foreach (var s in pagedSessions)
        {
            if (lastDate == null || s.StartTime.Date != lastDate.Value.Date)
            {
                vms.Add(new { IsDivider = true, DateDisplay = s.StartTime.ToString("dd MMMM, yyyy").ToUpper() });
                lastDate = s.StartTime.Date;
            }
            vms.Add(new SessionViewModel(s));
        }

        SessionList.ItemsSource = vms;
        TxtNoSessions.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaginationPanel.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SessionFilter_Changed(object s, EventArgs e)
    {
        _sessionPage = 1;
        LoadSessions();
    }

    private void BtnClearSessionFilters_Click(object s, RoutedEventArgs e)
    {
        TxtSessionFilterProject.Text = "";
        DateSessionFilter.SelectedDate = null;
        _sessionPage = 1;
        LoadSessions();
    }

    private void BtnPrevPage_Click(object s, RoutedEventArgs e)
    {
        if (_sessionPage > 1)
        {
            _sessionPage--;
            LoadSessions();
        }
    }

    private void BtnNextPage_Click(object s, RoutedEventArgs e)
    {
        if (_sessionPage < _sessionTotalPages)
        {
            _sessionPage++;
            LoadSessions();
        }
    }

    private void DeleteSession_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if (s is not WpfButton btn || !int.TryParse(btn.Tag?.ToString(), out int id)) return;
        if (ConfirmDialog.Show(this, "Eliminar sesión", $"¿Eliminar la sesión #{id} y todos sus datos?\nEsta acción no se puede deshacer."))
        {
            App.Database.DeleteSession(id);
            LoadSessions();
        }
    }

    private void SessionRow_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border { Tag: SessionViewModel vm }) OpenSessionDetail(vm);
    }

    private SessionViewModel? _detailVm;
    private void OpenSessionDetail(SessionViewModel vm)
    {
        _detailVm = vm;
        var session = vm.Raw;
        TxtDetailTitle.Text    = vm.DisplayName;
        TxtDetailSubtitle.Text = session.StartTime.ToString("dddd dd/MM/yyyy");
        TxtDetailName.Text     = session.Name;
        TxtDetailStart.Text    = session.StartTime.ToString("HH:mm:ss");
        TxtDetailEnd.Text      = session.EndTime.HasValue ? session.EndTime.Value.ToString("HH:mm:ss") : "Activa";
        TxtDetailDuration.Text = FormatDuration((session.EndTime ?? DateTime.Now) - session.StartTime);

        // Populate project combobox
        CmbDetailProject.Items.Clear();
        CmbDetailProject.Items.Add(new ComboBoxItem { Content = "Sin proyecto", Tag = (int?)null });
        foreach (var p in _projects)
        {
            var item = new ComboBoxItem { Content = p.Name, Tag = (int?)p.Id };
            CmbDetailProject.Items.Add(item);
            if (session.ProjectId == p.Id) CmbDetailProject.SelectedItem = item;
        }
        if (session.ProjectId == null) CmbDetailProject.SelectedIndex = 0;

        var detail = App.Database.GetSessionDetail(session.Id)
            .Select(x => new UsageSummaryViewModel
            {
                ProcessName    = x.ProcessName,
                AppDisplayName = x.AppDisplayName,
                FocusTime      = x.FocusTime,
                UnfocusTime    = x.UnfocusTime,
                EventCount     = x.EventCount,
                IsUrl          = x.IsUrl
            }).ToList();
        DetailAppsTable.ItemsSource = detail;
        TxtDetailNoApps.Visibility  = detail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPage("SessionDetail");
    }

    private void SaveSessionDetail_Click(object s, RoutedEventArgs e)
    {
        if (_detailVm == null) return;
        var name = TxtDetailName.Text.Trim();
        App.Database.UpdateSessionName(_detailVm.Id, name);

        int? projectId = null;
        if (CmbDetailProject.SelectedItem is ComboBoxItem { Tag: int pid }) projectId = pid;
        App.Database.UpdateSessionProject(_detailVm.Id, projectId);

        ToastWindow.Show("Cambios guardados", "Nombre y proyecto actualizados correctamente.");
    }

    // ── Projects ──────────────────────────────────────────────────────────
    private void LoadProjects()
    {
        _projects = App.Database.GetAllProjects();
        bool canStart = !App.Tracker.IsTracking;
        var vms = _projects.Select(p => new ProjectViewModel(p) { CanStart = canStart }).ToList();
        _loadingProjects = true;
        ProjectList.ItemsSource  = vms;
        _loadingProjects = false;
        TxtNoProjects.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NewProject_Click(object s, RoutedEventArgs e)
    {
        _currentProject = new Project { Name = "Nuevo proyecto", TrackedKeys = "" };
        OpenProjectDetail(_currentProject, isNew: true);
    }

    private void ProjectRow_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border { Tag: ProjectViewModel vm })
        {
            _currentProject = vm.Raw;
            OpenProjectDetail(_currentProject, isNew: false);
        }
    }

    private List<InstalledApp> _projectAppList = new();

    private void OpenProjectDetail(Project proj, bool isNew)
    {
        _currentProject = proj;
        if (isNew)
        {
            TxtProjectEditTitle.Text   = "Nuevo proyecto";
            TxtProjectName.Text        = "";
            BtnDeleteProject.Visibility = Visibility.Collapsed;
            BtnSaveProject.Content      = "Crear proyecto";
            LoadProjectOptions(proj);
            RefreshProjectChips(proj);
            TxtProjectSearch.Text = "";
            ProjectAppListContainer.Visibility = Visibility.Collapsed;
            ShowPage("ProjectEdit");
        }
        else
        {
            TxtProjectDetailTitle.Text = proj.Name;
            RefreshProjectViewChips(proj);
            LoadProjectStats(proj);
            UpdateProjectAlarmView(proj);
            ShowPage("ProjectDetail");
        }
    }

    private void UpdateProjectAlarmView(Project proj)
    {
        bool hasTotal = proj.TotalTimeAlarmSeconds.HasValue;
        bool hasSession = proj.SessionTimeAlarmSeconds.HasValue;

        ProjectAlarmSummary.Visibility = (hasTotal || hasSession) ? Visibility.Visible : Visibility.Collapsed;

        if (hasTotal)
        {
            BadgeTotalAlarm.Visibility = Visibility.Visible;
            TxtViewTotalAlarm.Text = FormatAlarmValue(proj.TotalTimeAlarmSeconds ?? 0);
        }
        else BadgeTotalAlarm.Visibility = Visibility.Collapsed;

        if (hasSession)
        {
            BadgeSessionAlarm.Visibility = Visibility.Visible;
            TxtViewSessionAlarm.Text = FormatAlarmValue(proj.SessionTimeAlarmSeconds ?? 0);
        }
        else BadgeSessionAlarm.Visibility = Visibility.Collapsed;
    }

    private string FormatAlarmValue(int secs)
    {
        if (secs >= 3600 && secs % 3600 == 0) return $"{secs / 3600}h";
        return $"{secs / 60}m";
    }

    private void GoToProjectEdit_Click(object s, RoutedEventArgs e)
    {
        if (_currentProject == null) return;
        TxtProjectEditTitle.Text    = _currentProject.Name;
        TxtProjectName.Text         = _currentProject.Name;
        BtnDeleteProject.Visibility = Visibility.Visible;
        BtnSaveProject.Content      = "Guardar proyecto";
        LoadProjectOptions(_currentProject);
        RefreshProjectChips(_currentProject);
        TxtProjectSearch.Text = "";
        ProjectAppListContainer.Visibility = Visibility.Collapsed;
        ShowPage("ProjectEdit");
    }

    private void LoadProjectOptions(Project proj)
    {
        var optKey = proj.TrackedKeys.Split(',')
            .FirstOrDefault(k => k.StartsWith("[opt:"));
        bool unfocus = false, unify = false;
        if (optKey != null)
        {
            unfocus = optKey.Contains("unfocus=1");
            unify   = optKey.Contains("unify=1");
        }
        if (ToggleProjUnfocus != null) ToggleProjUnfocus.IsChecked = unfocus;
        if (ToggleProjUnify   != null) ToggleProjUnify.IsChecked   = unify;

        if (proj.TotalTimeAlarmSeconds.HasValue)
        {
            bool isHrs = proj.TotalTimeAlarmSeconds.Value >= 3600 && proj.TotalTimeAlarmSeconds.Value % 3600 == 0;
            TxtTotalAlarm.Text = isHrs ? (proj.TotalTimeAlarmSeconds.Value / 3600).ToString() : (proj.TotalTimeAlarmSeconds.Value / 60).ToString();
            ToggleTotalUnit.IsChecked = isHrs;
        }
        else { TxtTotalAlarm.Text = ""; ToggleTotalUnit.IsChecked = false; }

        if (proj.SessionTimeAlarmSeconds.HasValue)
        {
            bool isHrs = proj.SessionTimeAlarmSeconds.Value >= 3600 && proj.SessionTimeAlarmSeconds.Value % 3600 == 0;
            TxtSessionAlarm.Text = isHrs ? (proj.SessionTimeAlarmSeconds.Value / 3600).ToString() : (proj.SessionTimeAlarmSeconds.Value / 60).ToString();
            ToggleSessionUnit.IsChecked = isHrs;
        }
        else { TxtSessionAlarm.Text = ""; ToggleSessionUnit.IsChecked = false; }
    }

    private void BackFromProjectEdit_Click(object s, RoutedEventArgs e)
    {
        if (_currentProject?.Id > 0)
        {
            // Return to detail view after editing existing project
            TxtProjectDetailTitle.Text = _currentProject.Name;
            RefreshProjectViewChips(_currentProject);
            LoadProjectStats(_currentProject);
            ShowPage("ProjectDetail");
        }
        else
        {
            ShowPage("Projects");
            LoadProjects();
        }
    }

    // Comenzar from project list — disables all other Comenzar buttons via ItemsControl refresh
    private void ProjectListStart_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if (s is not WpfButton btn || btn.Tag is not ProjectViewModel vm) return;
        if (App.Tracker.IsTracking) return;
        _currentProject = vm.Raw;
        BtnStartProjectSession_Click(s, e);
        // Refresh list so all Comenzar buttons get IsEnabled=False via binding
        LoadProjects();
    }

    // Pomodoro toggle in project list row
    private bool _loadingProjects = false;
    private void PomodoroToggle_Changed(object s, RoutedEventArgs e)
    {
        e.Handled = true; // prevent bubbling to ProjectRow_Click
        if (_loadingProjects) return;
        if (s is not System.Windows.Controls.Primitives.ToggleButton tb || tb.Tag is not ProjectViewModel vm) return;
        bool enabled = tb.IsChecked == true;
        vm.Raw.UsePomodoro = enabled;
        App.Database.SetProjectUsePomodoro(vm.Raw.Id, enabled);
        // Refresh _projects list so the in-memory cache stays consistent
        _projects = App.Database.GetAllProjects();
    }

    // Suppress mouse bubbling from the toggle StackPanel up to the row border
    private void IgnoreClick(object s, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    // Pencil icon in project list → go direct to edit
    private void ProjectEdit_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if (s is not WpfButton btn || btn.Tag is not ProjectViewModel vm) return;
        _currentProject = vm.Raw;
        TxtProjectEditTitle.Text    = _currentProject.Name;
        TxtProjectName.Text         = _currentProject.Name;
        BtnDeleteProject.Visibility = Visibility.Visible;
        BtnSaveProject.Content      = "Guardar proyecto";
        LoadProjectOptions(_currentProject);
        RefreshProjectChips(_currentProject);
        TxtProjectSearch.Text = "";
        ProjectAppListContainer.Visibility = Visibility.Collapsed;
        ShowPage("ProjectEdit");
    }

    private void RefreshProjectViewChips(Project proj)
    {
        ProjectViewChips.Children.Clear();
        var keys = proj.TrackedKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => !k.StartsWith("[opt:"))
            .ToArray();
        TxtProjectViewNoItems.Visibility = keys.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var key in keys)
        {
            var isUrl   = key.StartsWith("[") && key.EndsWith("]");
            var label   = isUrl ? MakeUrlLabel(key[1..^1]) : key;
            var display = isUrl ? $"🔗 {label}" : label;
            var bg  = isUrl ? "#1A2A44" : "#1A2200";
            var bdr = isUrl ? "#2A4A88" : "#4A6600";
            var fg  = isUrl ? "#60AAFF" : "#C8FF00";

            var chip = new Border
            {
                Background      = new WpfBrush(ParseHex(bg)),
                BorderBrush     = new WpfBrush(ParseHex(bdr)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(5),
                Padding         = new Thickness(8, 4, 8, 4),
                Margin          = new Thickness(0, 0, 6, 6)
            };
            chip.Child = new TextBlock
            {
                Text = display, FontSize = 12,
                Foreground = new WpfBrush(ParseHex(fg))
            };
            ProjectViewChips.Children.Add(chip);
        }
    }

    private void ToggleProjectEdit_Click(object s, RoutedEventArgs e) { } // replaced by GoToProjectEdit_Click

    private void BtnStartProjectSession_Click(object s, RoutedEventArgs e)
    {
        if (_currentProject == null || App.Tracker.IsTracking) return;

        var allKeys = _currentProject.TrackedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? k[..^4] : k)
            .ToArray();

        // Read saved options
        var optKey  = allKeys.FirstOrDefault(k => k.StartsWith("[opt:"));
        bool trackUnfocus = optKey?.Contains("unfocus=1") == true;
        bool unify        = optKey?.Contains("unify=1")   == true;

        var dataKeys = allKeys.Where(k => !k.StartsWith("[opt:")).ToArray();
        var apps = dataKeys.Where(k => !k.StartsWith("["))
                           .Select(k => new InstalledApp { ExecutableName = k, DisplayName = k, IsSelected = true, TrackUnfocus = trackUnfocus })
                           .ToList();
        var urls = dataKeys.Where(k => k.StartsWith("[") && k.EndsWith("]"))
                           .Select(k => { var h = k[1..^1]; return new TrackedUrl { Host = h, Label = MakeUrlLabel(h), IsSelected = true, TrackUnfocus = trackUnfocus }; })
                           .ToList();

        if (unify) apps = UnifyVersions(apps);

        App.Tracker.StartTracking(apps, urls, _currentProject.Name, _currentProject.Id,
                                  sessionAlarmMins: _currentProject.SessionTimeAlarmSeconds / 60,
                                  totalAlarmMins: _currentProject.TotalTimeAlarmSeconds / 60);
        PluginRegistry.Instance.NotifyTrackingStarted(_currentProject.Id);
        _sessionStart     = DateTime.Now;
        _pendingProjectId = _currentProject.Id;
        
        BtnSidebarStop.IsEnabled = true;
        StatusDot.Fill = (WpfBrush)FindResource("AccentBrush");
        TxtStatusLabel.Text = "TRACKING";
        TxtSessionTime.Text = "0s";
        TxtCurrentApp.Text  = "Esperando…";
        ToastWindow.Show("Sesión iniciada", $"Trackeando proyecto \"{_currentProject.Name}\"");

        // Reload project list so Comenzar buttons go grey
        if (PageProjects.Visibility == Visibility.Visible) LoadProjects();
        ShowPage("Setup");
    }

    private void RefreshProjectChips(Project proj)
    {
        ProjectChips.Children.Clear();
        var keys = proj.TrackedKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => !k.StartsWith("[opt:"))
            .ToList();
        TxtProjectNoItems.Visibility = keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var key in keys)
        {
            var cap   = key;
            var isUrl = cap.StartsWith("[") && cap.EndsWith("]");
            var label = isUrl ? MakeUrlLabel(cap[1..^1]) : cap;
            var displayLabel = isUrl ? $"🔗 {label}" : label;

            ProjectChips.Children.Add(MakeChip(displayLabel, isUrl, false, () =>
            {
                var updated = proj.TrackedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Where(k => k != cap).ToList();
                proj.TrackedKeys = string.Join(",", updated);
                RefreshProjectChips(proj);
            }));
        }
    }

    private void TxtProjectSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        var query = TxtProjectSearch.Text.Trim();
        if (string.IsNullOrEmpty(query)) { ProjectAppListContainer.Visibility = Visibility.Collapsed; return; }

        var maybeHost = UrlReaderService.NormalizeHost(query);
        var results   = new List<InstalledApp>();

        results.AddRange(_allApps.Where(a =>
            a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            a.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase)));

        if (maybeHost != null)
            results.Insert(0, new InstalledApp { DisplayName = $"🔗 {MakeUrlLabel(maybeHost)}", ExecutableName = $"[{maybeHost}]" });

        _projectAppList = results;
        ProjectAppList.ItemsSource = results;
        ProjectAppListContainer.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddToProject_Click(object s, RoutedEventArgs e)
    {
        if (_currentProject == null) return;
        var existing = _currentProject.TrackedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = _projectAppList.Where(a => a.IsSelected).Select(a => a.ExecutableName).Where(k => !existing.Contains(k));
        _currentProject.TrackedKeys = string.Join(",", existing.Concat(toAdd));
        TxtProjectSearch.Text = "";
        ProjectAppListContainer.Visibility = Visibility.Collapsed;
        RefreshProjectChips(_currentProject);
    }

    private void BackToProjects_Click(object s, RoutedEventArgs e) { ShowPage("Projects"); LoadProjects(); }

    private void SaveProject_Click(object s, RoutedEventArgs e)
    {
        if (_currentProject == null) return;
        var name = TxtProjectName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ToastWindow.Show("Nombre requerido", "El nombre del proyecto no puede estar vacío.", ToastKind.Warning);
            return;
        }
        _currentProject.Name = name;

        // Alarms
        int totalVal   = int.TryParse(TxtTotalAlarm.Text,   out int ta) ? ta : 0;
        int sessionVal = int.TryParse(TxtSessionAlarm.Text, out int sa) ? sa : 0;

        _currentProject.TotalTimeAlarmSeconds   = totalVal > 0   ? (int?)(totalVal   * (ToggleTotalUnit.IsChecked == true ? 3600 : 60)) : null;
        _currentProject.SessionTimeAlarmSeconds = sessionVal > 0 ? (int?)(sessionVal * (ToggleSessionUnit.IsChecked == true ? 3600 : 60)) : null;

        // Persist unfocus/unify flags in TrackedKeys metadata prefix
        // Format: "[opt:unfocus=1;unify=1]key1,key2,..."
        var unfocus = ToggleProjUnfocus?.IsChecked == true ? 1 : 0;
        var unify   = ToggleProjUnify?.IsChecked   == true ? 1 : 0;
        var keys    = _currentProject.TrackedKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => !k.StartsWith("[opt:"))
            .ToList();
        if (unfocus == 1 || unify == 1)
            keys.Insert(0, $"[opt:unfocus={unfocus};unify={unify}]");
        _currentProject.TrackedKeys = string.Join(",", keys);

        if (_currentProject.Id == 0)
            _currentProject.Id = App.Database.CreateProject(name, _currentProject.TrackedKeys, _currentProject.TotalTimeAlarmSeconds, _currentProject.SessionTimeAlarmSeconds);
        else
            App.Database.UpdateProject(_currentProject.Id, name, _currentProject.TrackedKeys, _currentProject.TotalTimeAlarmSeconds, _currentProject.SessionTimeAlarmSeconds);

        ToastWindow.Show("Proyecto guardado", "Los cambios fueron guardados correctamente.");
        _projects = App.Database.GetAllProjects();
        RefreshLastProjectCard();

        // Navigate to detail view after save
        TxtProjectDetailTitle.Text = _currentProject.Name;
        RefreshProjectViewChips(_currentProject);
        LoadProjectStats(_currentProject);
        ShowPage("ProjectDetail");
    }

    private void DeleteProject_Click(object s, RoutedEventArgs e)
    {
        if (_currentProject == null || _currentProject.Id == 0) return;
        if (ConfirmDialog.Show(this, "Eliminar proyecto", $"¿Eliminar el proyecto \"{_currentProject.Name}\"?\nLas sesiones no se borran — solo se desvinculan del proyecto."))
        {
            App.Database.DeleteProject(_currentProject.Id);
            _currentProject = null;
            ShowPage("Projects");
            LoadProjects();
            RefreshLastProjectCard();
        }
    }

    private void BtnResetAllData_Click(object s, RoutedEventArgs e)
    {
        if (ConfirmDialog.Show(this, "RESTABLECER TODO", "Esto borrará permanentemente todos tus datos, proyectos, sesiones y reiniciará el tutorial.\n\n¿Estás COMPLETAMENTE seguro?", danger: true))
        {
            if (App.Tracker.IsTracking) App.Tracker.StopTracking();

            // Clear DB
            App.Database.ResetDatabase();

            // Clear Local state
            _projects.Clear();
            _currentProject = null;
            _pendingProjectId = null;

            // Clear Onboarding knowledge
            OnboardingWindow.ResetStatus();

            // Refresh UI
            RefreshHomeRecentProjects();
            LoadProjects();
            
            ToastWindow.Show("App reiniciada", "Todos los datos han sido borrados correctamente.");
            
            // Go home and show onboarding
            ShowPage("Setup");
            ShowOnboardingOverlay();
        }
    }

    private void LoadProjectStats(Project proj)
    {
        var (from, to) = GetDateRange(_currentProjTimeTab, ProjDateFrom, ProjDateTo);
        var (main, extras) = App.Database.GetProjectSummaries(proj, from, to);

        ProjectMainTable.ItemsSource = main.Select(x => new UsageSummaryViewModel
        {
            ProcessName = x.ProcessName, AppDisplayName = x.AppDisplayName,
            FocusTime = x.FocusTime, UnfocusTime = x.UnfocusTime,
            EventCount = x.EventCount, IsUrl = x.IsUrl
        }).ToList();
        TxtProjectNoData.Visibility = main.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ProjectExtrasTable.ItemsSource = extras.Select(x => new UsageSummaryViewModel
        {
            ProcessName = x.ProcessName, AppDisplayName = x.AppDisplayName,
            FocusTime = x.FocusTime, UnfocusTime = x.UnfocusTime, IsUrl = x.IsUrl
        }).ToList();
        ProjectExtrasSection.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjTimeTab_Click(object s, RoutedEventArgs e)
    {
        if (s is not ToggleButton btn) return;
        foreach (var t in _projTimeTabs) t.IsChecked = false;
        btn.IsChecked = true;
        _currentProjTimeTab = btn.Tag?.ToString() ?? "Today";
        ProjCustomDatePanel.Visibility = _currentProjTimeTab == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        if (_currentProjTimeTab != "Custom" && _currentProject != null) LoadProjectStats(_currentProject);
    }

    private void ProjDateRange_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_currentProject != null) LoadProjectStats(_currentProject);
    }

    // ── Shared helpers ────────────────────────────────────────────────────
    private static (DateTime from, DateTime to) GetDateRange(string tab, DatePicker? from, DatePicker? to)
    {
        var now = DateTime.Now;
        return tab switch
        {
            "Today"  => (now.Date, now),
            "Week"   => (now.Date.AddDays(-(int)now.DayOfWeek), now),
            "Month"  => (new DateTime(now.Year, now.Month, 1), now),
            "Year"   => (new DateTime(now.Year, 1, 1), now),
            "Custom" => (from?.SelectedDate?.Date ?? now.Date.AddDays(-7),
                         to?.SelectedDate?.Date.AddDays(1) ?? now),
            _ => (now.Date, now)
        };
    }

    private static string MakeUrlLabel(string host)
    {
        var parts = host.Split('.');
        if (parts.Length > 0 && parts[0].Length > 0)
            parts[0] = char.ToUpper(parts[0][0]) + parts[0][1..];
        return string.Join(".", parts);
    }

    private static Border MakeChip(string text, bool isUrl, bool trackUnfocus, Action onRemove)
    {
        var bg  = isUrl ? "#1A2A44" : "#1A2200";
        var bdr = isUrl ? "#2A4A88" : "#4A6600";
        var fg  = isUrl ? "#60AAFF" : "#C8FF00";

        var chip = new Border
        {
            Background      = new WpfBrush(ParseHex(bg)),
            BorderBrush     = new WpfBrush(ParseHex(bdr)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new System.Windows.CornerRadius(5),
            Padding         = new Thickness(8, 4, 8, 4),
            Margin          = new Thickness(0, 0, 6, 6),
            Cursor          = WpfCursors.Hand
        };
        var sp = new StackPanel { Orientation = WpfOrientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12,
            Foreground = new WpfBrush(ParseHex(fg)),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (trackUnfocus)
        {
            sp.Children.Add(new TextBlock
            {
                Text = " ·U", FontSize = 10,
                Foreground = new WpfBrush(ParseHex("#FFAA44")),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        sp.Children.Add(new TextBlock
        {
            Text = "  ✕", FontSize = 11,
            Foreground = new WpfBrush(ParseHex(fg)) { Opacity = 0.6 },
            VerticalAlignment = VerticalAlignment.Center
        });
        chip.Child = sp;
        chip.MouseLeftButtonUp += (_, _) => onRemove();
        return chip;
    }

    private static WpfColor ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return WpfColor.FromRgb(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    // ── Status / timer ────────────────────────────────────────────────────
    private void SetStatusDot(bool active)
    {
        var accent = WpfColor.FromRgb(200, 255, 0);
        var muted  = WpfColor.FromRgb(80, 80, 95);
        StatusDot.Fill            = new WpfBrush(active ? accent : muted);
        TxtStatusLabel.Text       = active ? "ACTIVO" : "INACTIVO";
        TxtStatusLabel.Foreground = new WpfBrush(active ? accent : muted);
    }

    private void UiTimer_Tick(object? s, EventArgs e)
    {
        bool isTracking = App.Tracker.IsTracking;
        
        // Update sidebar stop button state/color
        BtnSidebarStop.IsEnabled = isTracking;
        var stopIcon = (BtnSidebarStop.Content as StackPanel)?.Children[0] as TextBlock;
        var stopText = (BtnSidebarStop.Content as StackPanel)?.Children[1] as TextBlock;
        
        var muted  = (WpfBrush)FindResource("TextMutedBrush");
        var danger = new WpfBrush(ParseHex("#FF4D6A"));
        var accent = (WpfBrush)FindResource("AccentBrush");
        var bgDark = new WpfBrush(ParseHex("#0F0F13"));

        if (isTracking)
        {
            if (stopIcon != null) stopIcon.Foreground = danger;
            if (stopText != null) stopText.Foreground = danger;
        }
        else
        {
            if (stopIcon != null) stopIcon.Foreground = muted;
            if (stopText != null) stopText.Foreground = muted;
        }

        // Disable Lite Mode button if tracking
        if (BtnEnterLite != null) BtnEnterLite.IsEnabled = !isTracking;

        if (isTracking)
        {
            var elapsed = DateTime.Now - _sessionStart;
            TxtSessionTime.Text = FormatDuration(elapsed);

            // Visual alarm feedback (Session)
            bool sessionAlarmReached = App.Tracker.SessionAlarmSeconds.HasValue && 
                                       elapsed.TotalSeconds >= App.Tracker.SessionAlarmSeconds.Value;
            
            // Visual alarm feedback (Total)
            bool totalAlarmReached = false;
            if (App.Tracker.TotalAlarmSeconds.HasValue)
            {
                // Current session total (Focus + Unfocus)
                // For simplicity, let's use the session duration as a proxy for the total project time increase
                // A more accurate way would be to sum all events from DB for this session
                totalAlarmReached = (App.Tracker.InitialTotalSeconds + elapsed.TotalSeconds) >= App.Tracker.TotalAlarmSeconds.Value;
            }

            bool anyAlarmReached = sessionAlarmReached || totalAlarmReached;
            var statusColor = anyAlarmReached ? danger : accent;

            if (anyAlarmReached)
            {
                TxtSessionTime.Foreground = danger;
                StatusDot.Fill = danger;
            }
            else
            {
                TxtSessionTime.Foreground = accent;
                StatusDot.Fill = accent;
            }

            // Update Alarm Indicators in Home Dashboard
            if (LiveAlarmSession.Visibility == Visibility.Visible)
            {
                var fg = sessionAlarmReached ? danger : muted;
                ((StackPanel)LiveAlarmSession).Children.OfType<TextBlock>().ToList().ForEach(t => t.Foreground = fg);
            }
            if (LiveAlarmTotal.Visibility == Visibility.Visible)
            {
                var fg = totalAlarmReached ? danger : muted;
                ((StackPanel)LiveAlarmTotal).Children.OfType<TextBlock>().ToList().ForEach(t => t.Foreground = fg);
            }

            // Sidebar collapsed state: paint entire block
            if (_sidebarCollapsed)
            {
                SidebarStatusArea.Background = statusColor;
                StatusDot.Visibility = Visibility.Collapsed;
            }
            else
            {
                SidebarStatusArea.Background = bgDark;
                StatusDot.Visibility = Visibility.Visible;
            }

            // Real-time refresh of live activity
            if (PageSetup.Visibility == Visibility.Visible && HomeLiveDashboard.Visibility == Visibility.Visible)
            {
                RefreshLiveDashboardStats();
            }
        }

        // Refresh plugin button visibility on the Home screen every tick
        if (PageSetup.Visibility == Visibility.Visible)
        {
            RefreshPluginButtonVisibility(PluginBtnsHome);
        }

        if (!isTracking)
        {
            StatusDot.Fill = muted;
            if (_sidebarCollapsed)
            {
                SidebarStatusArea.Background = muted;
                StatusDot.Visibility = Visibility.Collapsed;
            }
            else
            {
                SidebarStatusArea.Background = bgDark;
                StatusDot.Visibility = Visibility.Visible;
            }
            TxtSessionTime.Text = "—";
            TxtSessionTime.Foreground = accent;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (App.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (App.TrayIcon != null && !_trayHintShown)
            {
                _trayHintShown = true;
                App.TrayIcon.ShowBalloonTip(3000, "Focus Tracker sigue activo",
                    "El tracking continúa en segundo plano.\nDoble click en el ícono para volver.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        // else: allow normal close (base.OnClosing handles shutdown)
    }

    // ══ ONBOARDING OVERLAY ════════════════════════════════════════════════
    private int _obStep = 1;
    private const int OB_TOTAL = 4;
    private bool _sidebarCollapsed = false;

    private void BtnCollapseSidebar_Click(object s, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        double newWidth = _sidebarCollapsed ? 64 : 220;
        Sidebar.Width = newWidth;
        ColSidebar.Width = new GridLength(newWidth);

        // Hide/Show text elements
        var vis = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        TxtLogo.Visibility          = vis;
        SidebarStatusText.Visibility = vis;
        TxtNavInicio.Visibility     = vis;
        TxtNavDash.Visibility       = vis;
        TxtNavProjects.Visibility   = vis;
        TxtNavPlugins.Visibility    = vis;
        TxtNavSettings.Visibility   = vis;
        TxtNavHelp.Visibility       = vis;
        TxtStopLabel.Visibility     = vis;
        TxtResumeLabel.Visibility   = vis;

        // Adjust layout for collapsed mode
        ColStatusDot.Width = _sidebarCollapsed ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SidebarStatusArea.Padding = _sidebarCollapsed ? new Thickness(0, 12, 0, 12) : new Thickness(18, 12, 18, 12);

        // Trigger immediate update of sidebar background
        UiTimer_Tick(null, EventArgs.Empty);

        // Adjust button padding for icons only, keeping vertical height consistent
        var padding = _sidebarCollapsed ? new Thickness(0, 10, 0, 10) : new Thickness(10, 10, 10, 10);
        
        foreach (var btn in new[] { BtnNavSetup, BtnNavDashboard, BtnNavProjects, BtnNavPlugins, BtnNavSettings, BtnNavHelp, BtnSidebarResume, BtnSidebarStop })
        {
            btn.Padding = padding;
            if (_sidebarCollapsed)
                btn.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
            else
                btn.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
        }
    }

    private void NumericOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void ProjectCard_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (s is FrameworkElement el && el.Tag is Project proj)
        {
            OpenProjectDetail(proj, false);
        }
    }

    public void ShowOnboardingOverlay()
    {
        _obStep = 1;
        OB_Step1.Visibility = Visibility.Visible;
        OB_Step2.Visibility = Visibility.Collapsed;
        OB_Step3.Visibility = Visibility.Collapsed;
        OB_Step4.Visibility = Visibility.Collapsed;

        // Ensure the back button is hidden for step 1 if we add it globally, 
        // but it's better to add it to each step's grid.

        // Clear frozen animation from prior fade-out so Opacity is writable again
        OnboardingOverlay.BeginAnimation(OpacityProperty, null);
        OnboardingOverlay.Opacity           = 1;
        OnboardingOverlay.IsHitTestVisible  = true;
        OnboardingOverlay.Visibility        = Visibility.Visible;
        System.Windows.Controls.Panel.SetZIndex(OnboardingOverlay, 200);
    }

    private void OB_Next_Click(object s, RoutedEventArgs e)
    {
        _obStep++;
        UpdateOnboardingStep();
        if (_obStep > OB_TOTAL) FinishOnboarding();
    }

    private void OB_Back_Click(object s, RoutedEventArgs e)
    {
        if (_obStep > 1)
        {
            _obStep--;
            UpdateOnboardingStep();
        }
    }

    private void OB_Dot_Click(object s, RoutedEventArgs e)
    {
        if (s is WpfButton btn && int.TryParse(btn.Tag?.ToString(), out int step))
        {
            _obStep = step;
            UpdateOnboardingStep();
        }
    }

    private void UpdateOnboardingStep()
    {
        OB_Step1.Visibility = _obStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        OB_Step2.Visibility = _obStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        OB_Step3.Visibility = _obStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        OB_Step4.Visibility = _obStep == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OB_Skip_Click(object s, RoutedEventArgs e)   => FinishOnboarding();
    private void OB_Finish_Click(object s, RoutedEventArgs e) => FinishOnboarding();

    private void FinishOnboarding()
    {
        OnboardingWindow.MarkCompleted();
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            TimeSpan.FromMilliseconds(500))
        {
            // FillBehavior.Stop releases the Opacity property after animation ends
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            OnboardingOverlay.Visibility      = Visibility.Collapsed;
            OnboardingOverlay.IsHitTestVisible = false;
            OnboardingOverlay.Opacity         = 0;
        };
        OnboardingOverlay.BeginAnimation(OpacityProperty, anim);
    }

    // ══ LITE MODE ═════════════════════════════════════════════════════════
#pragma warning disable CS0414
    private bool _liteMode = false;
#pragma warning restore CS0414
    private List<InstalledApp> _liteAllApps  = new();
    private List<TrackedUrl>   _liteUrls     = new();
    private int  _liteSessionId;

    private void EnterLiteMode_Click(object s, RoutedEventArgs e)
    {
        _liteMode = true;

        // Save current size to restore later
        _normalWidth  = Width;
        _normalHeight = Height;

        // Hide sidebar, expand pages grid to full window
        Sidebar.Visibility = Visibility.Collapsed;
        Grid.SetColumn(PagesGrid, 0);
        Grid.SetColumnSpan(PagesGrid, 2);

        // Resize window for Lite mode - even smaller
        Width     = 420;
        Height    = 580;
        MinWidth  = 420;
        MinHeight = 580;

        // Reset lite state
        _liteUrls.Clear();
        TxtLiteSearch.Text = "";
        LiteListContainer.Visibility  = Visibility.Collapsed;
        LiteSelectedPanel.Visibility  = Visibility.Collapsed;
        LiteChips.Children.Clear();
        TxtLiteEmpty.Visibility = Visibility.Visible;
        BtnLiteStart.IsEnabled = false;
        BtnLiteStop.IsEnabled  = false;

        if (_allApps.Count == 0) _liteAllApps = InstalledAppsHelper.GetInstalledApps();
        else _liteAllApps = _allApps.Select(a => { a.IsSelected = false; return a; }).ToList();

        ShowPage("Lite");
    }

    private double _normalWidth  = 1050;
    private double _normalHeight = 700;

    private void ExitLiteMode_Click(object s, RoutedEventArgs e)
    {
        if (App.Tracker.IsTracking) { App.Tracker.StopTracking(); PluginRegistry.Instance.NotifyTrackingStopped(); }
        _liteMode = false;

        // Restore window size
        MinWidth  = 860;
        MinHeight = 600;
        Width     = _normalWidth;
        Height    = _normalHeight;

        // Restore sidebar
        Sidebar.Visibility = Visibility.Visible;
        Grid.SetColumn(PagesGrid, 1);
        Grid.SetColumnSpan(PagesGrid, 1);
        BtnNavDashboard.Visibility = Visibility.Visible;
        BtnNavProjects.Visibility  = Visibility.Visible;
        BtnNavHelp.Visibility      = Visibility.Visible;
        BtnSidebarStop.Visibility  = Visibility.Visible;
        ShowPage("Setup");
    }

    private void TxtLiteSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        var query = TxtLiteSearch.Text.Trim();
        if (string.IsNullOrEmpty(query)) { LiteListContainer.Visibility = Visibility.Collapsed; return; }

        var results = new List<InstalledApp>();
        var maybeHost = UrlReaderService.NormalizeHost(query);
        if (maybeHost != null && !_liteUrls.Any(u => u.Host.Equals(maybeHost, StringComparison.OrdinalIgnoreCase)))
            results.Add(new InstalledApp { DisplayName = $"🔗  {MakeUrlLabel(maybeHost)}", ExecutableName = $"[url:{maybeHost}]" });
        results.AddRange(_liteAllApps.Where(a =>
            a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            a.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase)));

        LiteListContainer.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LiteAppList.ItemsSource = results;
    }

    private void LiteCheckBox_Changed(object s, RoutedEventArgs e)
    {
        if (s is not WpfCheckBox cb || cb.DataContext is not InstalledApp app) return;
        if (app.ExecutableName.StartsWith("[url:", StringComparison.OrdinalIgnoreCase))
        {
            var host = app.ExecutableName[5..^1];
            if (cb.IsChecked == true && !_liteUrls.Any(u => u.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                _liteUrls.Add(new TrackedUrl { Host = host, Label = MakeUrlLabel(host), IsSelected = true });
            else if (cb.IsChecked == false)
                _liteUrls.RemoveAll(u => u.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        }
        TxtLiteSearch.Text = "";
        LiteListContainer.Visibility = Visibility.Collapsed;
        RefreshLiteChips();
    }

    private void RefreshLiteChips()
    {
        LiteChips.Children.Clear();
        var apps = _liteAllApps.Where(a => a.IsSelected).ToList();
        var total = apps.Count + _liteUrls.Count;
        LiteSelectedPanel.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtLiteEmpty.Visibility      = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnLiteStart.IsEnabled = total > 0 && !App.Tracker.IsTracking;

        foreach (var app in apps)
        {
            var cap = app;
            LiteChips.Children.Add(MakeChip(cap.DisplayName, false, false, () =>
            {
                cap.IsSelected = false;
                RefreshLiteChips();
            }));
        }
        foreach (var url in _liteUrls.ToList())
        {
            var cap = url;
            LiteChips.Children.Add(MakeChip($"🔗 {cap.Label}", true, false, () =>
            {
                _liteUrls.Remove(cap);
                RefreshLiteChips();
            }));
        }
    }

    private void BtnLiteStart_Click(object s, RoutedEventArgs e)
    {
        // Lite mode always tracks Focus + Unfocus
        var apps = _liteAllApps.Where(a => a.IsSelected)
                               .Select(a => { a.TrackUnfocus = true; return a; }).ToList();
        var urls = _liteUrls.Select(u => { u.TrackUnfocus = true; return u; }).ToList();
        if (!apps.Any() && !urls.Any()) return;

        int? sessionAlarm = int.TryParse(TxtLiteAlarm.Text, out int sa) ? (int?)sa : null;
        if (sessionAlarm.HasValue) sessionAlarm *= (ToggleLiteAlarmUnit.IsChecked == true ? 60 : 1);

        App.Tracker.StartTracking(apps, urls, sessionName: "[lite]", sessionAlarmMins: sessionAlarm);
        PluginRegistry.Instance.NotifyTrackingStarted(null);
        _liteSessionId  = App.Tracker.CurrentSessionId;
        _sessionStart   = DateTime.Now;
        BtnLiteStart.IsEnabled = false;
        BtnLiteStop.IsEnabled  = true;
        BtnSidebarStop.IsEnabled = true;
        SetStatusDot(true);
        TxtSessionTime.Text = "0s";
        TxtCurrentApp.Text  = "Esperando…";
    }

    private void BtnLiteStop_Click(object s, RoutedEventArgs e)
    {
        App.Tracker.StopTracking();
        PluginRegistry.Instance.NotifyTrackingStopped();
        BtnLiteStop.IsEnabled    = false;
        BtnSidebarStop.IsEnabled = false;
        SetStatusDot(false);
        TxtSessionTime.Text = "—";
        TxtCurrentApp.Text  = "—";

        // Show result page
        var dur = DateTime.Now - _sessionStart;
        TxtLiteResultSub.Text = $"Duración: {FormatDuration(dur)}  ·  {DateTime.Now:HH:mm}";
        var detail = App.Database.GetSessionDetail(_liteSessionId)
            .Select(x => new UsageSummaryViewModel
            {
                ProcessName = x.ProcessName, AppDisplayName = x.AppDisplayName,
                FocusTime = x.FocusTime, UnfocusTime = x.UnfocusTime, IsUrl = x.IsUrl
            }).ToList();
        LiteResultTable.ItemsSource = detail;
        ShowPage("LiteResult");
    }

    private void BtnLiteSave_Click(object s, RoutedEventArgs e)
    {
        // Session was already created by TrackingService — just keep it (update name)
        App.Database.UpdateSessionName(_liteSessionId, $"Lite {DateTime.Now:dd/MM HH:mm}");
        ToastWindow.Show("Sesión guardada", "Los datos de esta sesión quedaron en tu historial.");
        ShowPage("Lite");
        BtnLiteStart.IsEnabled = LiteChips.Children.Count > 0;
        BtnLiteStop.IsEnabled  = false;
    }

    private void BtnLiteDiscard_Click(object s, RoutedEventArgs e)
    {
        // Delete the ephemeral session
        App.Database.DeleteSession(_liteSessionId);
        ShowPage("Lite");
        BtnLiteStart.IsEnabled = LiteChips.Children.Count > 0;
        BtnLiteStop.IsEnabled  = false;
    }

    // ── Plugins ───────────────────────────────────────────────────────────

    // ── Plugin page — panel navigation ────────────────────────────────────

    private void LoadPluginsPage()
    {
        // When opening the plugins page, always start on the store view
        ShowPluginStore();
    }

    private void ShowPluginStore()
    {
        PluginStorePanel.Visibility      = Visibility.Visible;
        PluginMyPluginsPanel.Visibility  = Visibility.Collapsed;
        PluginHelpPanel.Visibility       = Visibility.Collapsed;
        PluginDetailPanel.Visibility     = Visibility.Collapsed;
        // Reset search
        TxtPluginSearch.Text = "";

        if (!_storeLoaded)
            // First open: fetch from API (RenderStoreList is called after load completes)
            _ = LoadPluginStoreAsync();
        else
            // Subsequent opens: always rebuild cards so installed/disabled state
            // reflects the current PluginManager state (e.g. after fresh install).
            // NOTE: TxtPluginSearch.Text="" above already fires TextChanged → RenderStoreList,
            // but only if the ComboBox filter is still "all". We force it here unconditionally.
            RenderStoreList(_storePluginsData);
    }

    private async Task LoadPluginStoreAsync()
    {
        // Show loading state
        if (TxtStoreLoading != null) TxtStoreLoading.Visibility = Visibility.Visible;
        if (TxtStoreError   != null) TxtStoreError.Visibility   = Visibility.Collapsed;
        if (PluginApiListPanel != null) PluginApiListPanel.Children.Clear();

        // Populate category filter from store data (populated after load)
        try
        {
            // Real PHP file — no URL rewriting needed on the server
            const string ApiUrl = "https://santiagorada.com/focus-tracker/api/plugins.php";
            var json = await _storeHttp.GetStringAsync(ApiUrl);

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            var plugins = new List<StorePluginDto>();
            foreach (var el in data.EnumerateArray())
            {
                // Category is nested: { "category": { "slug": "...", "name": "..." } }
                string catName = "", catSlug = "";
                if (el.TryGetProperty("category", out var catEl) && catEl.ValueKind == JsonValueKind.Object)
                {
                    catName = GetStr(catEl, "name");
                    catSlug = GetStr(catEl, "slug");
                }

                decimal price = 0m;
                if (el.TryGetProperty("price", out var prEl) && prEl.ValueKind == JsonValueKind.Number)
                    price = prEl.GetDecimal();

                plugins.Add(new StorePluginDto(
                    Id:            GetStr(el, "id"),
                    Name:          GetStr(el, "name"),
                    Author:        GetStr(el, "author"),
                    Version:       GetStr(el, "version"),
                    Description:   GetStr(el, "description"),
                    DownloadUrl:   GetStr(el, "download_url"),
                    DownloadCount: el.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out int n) ? n : 0,
                    CategoryName:  catName,
                    CategorySlug:  catSlug,
                    PricingType:   GetStr(el, "pricing_type") is "" ? "free" : GetStr(el, "pricing_type"),
                    Price:         price,
                    Currency:      GetStr(el, "currency") is "" ? "USD" : GetStr(el, "currency")));
            }

            _storePluginsData = plugins;
            _storeLoaded      = true;

            // Render all cards
            RenderStoreList(_storePluginsData);

            if (TxtStoreLoading != null) TxtStoreLoading.Visibility = Visibility.Collapsed;
            if (TxtStoreSection != null)
                TxtStoreSection.Text = $"TODOS LOS PLUGINS ({plugins.Count})";
        }
        catch (Exception ex)
        {
            if (TxtStoreLoading != null) TxtStoreLoading.Visibility = Visibility.Collapsed;
            if (TxtStoreError   != null)
            {
                TxtStoreError.Text       = $"No se pudo cargar la tienda: {ex.Message}";
                TxtStoreError.Visibility = Visibility.Visible;
            }
        }
    }

    // Category filter is handled by pricing type (matches the XAML ComboBox items)
    // No dynamic population needed — items are fixed: free/donation/paid/subscription

    private void RenderStoreList(IEnumerable<StorePluginDto> plugins)
    {
        if (PluginApiListPanel == null) return;
        bool showInstalled = ChkShowInstalled?.IsChecked == true;
        var list = showInstalled
            ? plugins
            : plugins.Where(p => !IsPluginInstalled(p.Id));
        PluginApiListPanel.Children.Clear();
        foreach (var p in list)
            PluginApiListPanel.Children.Add(BuildStoreCard(p));
    }

    private System.Windows.Media.Brush Br(string key) =>
        (System.Windows.Media.Brush)(TryFindResource(key) ?? new WpfBrush(System.Windows.Media.Colors.Gray));

    private UIElement BuildStoreCard(StorePluginDto p)
    {
        bool isInstalled  = IsPluginInstalled(p.Id);
        var  resolvedCardId = isInstalled ? ResolvePluginId(p.Id) : p.Id;
        bool isDisabled   = isInstalled && FocusTracker.Plugins.PluginManager.Instance.IsDisabled(resolvedCardId);
        bool isInstalling = _installingIds.Contains(p.Id);

        var card = new Border
        {
            Background      = Br("BgCardBrush"),
            BorderBrush     = Br("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(20, 18, 20, 18),
            Margin          = new Thickness(0, 0, 0, 10),
            Cursor          = System.Windows.Input.Cursors.Hand
        };
        card.MouseDown += (_, e) => { if (!isInstalled && !isInstalling && e.ClickCount == 1) ShowPluginDetail(p); };

        var infoStack = new StackPanel();

        // ── Name + badge row ────────────────────────────────────────
        var nameRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        nameRow.Children.Add(new TextBlock
        {
            Text = p.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
        });

        // Pricing badge (free = no badge)
        string pricingLabel = PricingBadgeLabel(p);
        if (pricingLabel != "")
        {
            nameRow.Children.Add(new Border
            {
                Background = new WpfBrush(WpfColor.FromArgb(40, 200, 255, 0)),
                BorderBrush = new WpfBrush(WpfColor.FromArgb(80, 200, 255, 0)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = pricingLabel, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Br("AccentBrush") }
            });
        }

        if (isInstalled)
        {
            nameRow.Children.Add(new Border
            {
                Background = new WpfBrush(WpfColor.FromArgb(60, 42, 74, 106)),
                BorderBrush = new WpfBrush(WpfColor.FromArgb(120, 42, 74, 106)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = isDisabled ? "DESACTIVADO" : "INSTALADO", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Br("AccentBrush") }
            });
        }

        infoStack.Children.Add(nameRow);

        // ── Author · version ────────────────────────────────────────
        infoStack.Children.Add(new TextBlock
        {
            Text = $"por {p.Author}  ·  v{p.Version}",
            FontSize = 11, Foreground = Br("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 6)
        });

        // ── Description ─────────────────────────────────────────────
        infoStack.Children.Add(new TextBlock
        {
            Text = p.Description, FontSize = 12, Foreground = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 12)
        });

        // ── Action buttons ──────────────────────────────────────────
        var btnRow = new StackPanel { Orientation = WpfOrientation.Horizontal };

        if (isInstalling)
        {
            var prog = new System.Windows.Controls.ProgressBar
            {
                IsIndeterminate = true, Width = 110, Height = 30,
                Style = (Style?)TryFindResource(typeof(System.Windows.Controls.ProgressBar))
            };
            btnRow.Children.Add(prog);
        }
        else if (isInstalled)
        {
            // Toggle enabled/disabled
            var btnToggle = new WpfButton
            {
                Content = isDisabled ? "Activar" : "Desactivar",
                Style   = (Style)TryFindResource(isDisabled ? "PrimaryButton" : "OutlineButton")!,
                Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0)
            };
            btnToggle.Click += (_, _) =>
            {
                var resolvedId = ResolvePluginId(p.Id);
                string? err;
                if (FocusTracker.Plugins.PluginManager.Instance.IsDisabled(resolvedId))
                {
                    err = FocusTracker.Plugins.PluginManager.Instance.Enable(resolvedId);
                }
                else
                {
                    FocusTracker.Plugins.PluginRegistry.Instance.UnregisterContributions(resolvedId);
                    FocusTracker.Plugins.PluginManager.Instance.Disable(resolvedId);
                    err = null;
                }
                if (err != null) ShowToast($"Error: {err}", isError: true);
                LoadMyPluginsPanel();
                LoadPluginSettingCards();
                LoadHomePluginContent();
                RenderStoreList(_storePluginsData);
            };

            var btnUninstall = new WpfButton
            {
                Content = "Desinstalar",
                Style   = (Style)TryFindResource("DangerButton")!,
                Padding = new Thickness(14, 8, 14, 8)
            };
            btnUninstall.Click += (_, _) =>
            {
                bool confirmed = ConfirmDialog.Show(
                    this,
                    $"¿Desinstalar \"{p.Name}\"?",
                    "El plugin se eliminará permanentemente. Esta acción no se puede deshacer.",
                    "Desinstalar", danger: true);
                if (!confirmed) return;
                var resolvedId = ResolvePluginId(p.Id);
                FocusTracker.Plugins.PluginRegistry.Instance.UnregisterContributions(resolvedId);
                var err = FocusTracker.Plugins.PluginManager.Instance.Uninstall(resolvedId);
                if (err != null) { ShowToast($"Error: {err}", isError: true); return; }
                ForceDeletePluginFile(resolvedId);
                LoadMyPluginsPanel();
                LoadPluginSettingCards();
                LoadHomePluginContent();
                ShowToast($"\"{p.Name}\" desinstalado.");
                RenderStoreList(_storePluginsData);
            };

            btnRow.Children.Add(btnToggle);
            btnRow.Children.Add(btnUninstall);
        }
        else
        {
            var btnGet = new WpfButton
            {
                Content = "Obtener",
                Style   = (Style)TryFindResource("PrimaryButton")!,
                Padding = new Thickness(20, 8, 20, 8)
            };
            btnGet.Click += (_, _) => OpenPaymentModal(p);
            btnRow.Children.Add(btnGet);
        }

        infoStack.Children.Add(btnRow);

        // ── Download count (right) ──────────────────────────────────
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(infoStack, 0);
        outer.Children.Add(infoStack);

        var dlCount = new TextBlock
        {
            Text = p.DownloadCount >= 1000 ? $"{p.DownloadCount / 1000.0:0.#}k ↓" : $"{p.DownloadCount} ↓",
            FontSize = 11, Foreground = Br("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 4, 0, 0)
        };
        Grid.SetColumn(dlCount, 1);
        outer.Children.Add(dlCount);

        card.Child = outer;
        return card;
    }

    // ── Pricing helpers ────────────────────────────────────────────────────────
    private static string PricingBadgeLabel(StorePluginDto p) => p.PricingType switch
    {
        "paid"         => p.Price > 0 ? $"${p.Price:0.##}" : "Pago",
        "donation"     => "Donación",
        "subscription" => p.Price > 0 ? $"${p.Price:0.##}/mes" : "Suscripción",
        _              => ""   // free — no badge
    };

    private bool IsPluginInstalled(string id)
    {
        // 1. Check in-memory manifests (fast path, covers loaded plugins)
        if (FocusTracker.Plugins.PluginManager.Instance.GetAllManifests()
                .Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 2. Fallback: check if a .focusplugin file whose stem starts with "{id}-" or equals
        //    "{id}" exists on disk — handles plugins that failed to load into memory.
        //    Filename convention: "{pluginId}-{version}.focusplugin"
        var folder = FocusTracker.Plugins.PluginManager.Instance.PluginsFolder;
        if (!System.IO.Directory.Exists(folder)) return false;

        return System.IO.Directory.EnumerateFiles(folder, "*.focusplugin").Any(f =>
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(f);
            return string.Equals(stem, id, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() ?? ""
           : "";

    private void ShowMyPlugins()
    {
        PluginStorePanel.Visibility      = Visibility.Collapsed;
        PluginMyPluginsPanel.Visibility  = Visibility.Visible;
        PluginHelpPanel.Visibility       = Visibility.Collapsed;
        PluginDetailPanel.Visibility     = Visibility.Collapsed;
        LoadMyPluginsPanel();
    }

    private void ShowPluginHelp(string pluginId, string pluginName)
    {
        PluginStorePanel.Visibility      = Visibility.Collapsed;
        PluginMyPluginsPanel.Visibility  = Visibility.Collapsed;
        PluginHelpPanel.Visibility       = Visibility.Visible;
        PluginDetailPanel.Visibility     = Visibility.Collapsed;
        LoadPluginHelpContent(pluginId, pluginName);
    }

    // ── Button handlers ────────────────────────────────────────────────────

    private void BtnMyPlugins_Click(object s, RoutedEventArgs e)   => ShowMyPlugins();
    private void BtnBackToStore_Click(object s, RoutedEventArgs e) => ShowPluginStore();
    private void BtnBackToMyPlugins_Click(object s, RoutedEventArgs e)
    {
        PluginHelpPanel.Visibility       = Visibility.Collapsed;
        PluginMyPluginsPanel.Visibility  = Visibility.Visible;
        PluginStorePanel.Visibility      = Visibility.Collapsed;
    }

    // ── Search + filter ────────────────────────────────────────────────────

    private void TxtPluginSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        // Guard: may fire during XAML init before all named elements are ready
        if (PluginRecommendedPanel == null || PluginSearchResultsPanel == null || TxtPluginSearch == null)
            return;

        var query = TxtPluginSearch.Text.Trim();

        // Selected pricing-type filter ("all" | "free" | "paid" | "donation" | "subscription")
        var pricingFilter = (CmbPluginFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

        if (string.IsNullOrEmpty(query) && pricingFilter == "all")
        {
            PluginRecommendedPanel.Visibility   = Visibility.Visible;
            PluginSearchResultsPanel.Visibility = Visibility.Collapsed;
            // Re-render full store list in case filter changed
            RenderStoreList(_storePluginsData);
            return;
        }

        PluginRecommendedPanel.Visibility   = Visibility.Collapsed;
        PluginSearchResultsPanel.Visibility = Visibility.Visible;

        // Filter store plugins by query + pricing type + installed state
        bool showInstalled = ChkShowInstalled?.IsChecked == true;
        IEnumerable<StorePluginDto> results = _storePluginsData;

        if (!showInstalled)
            results = results.Where(p => !IsPluginInstalled(p.Id));

        if (!string.IsNullOrEmpty(query))
            results = results.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)         ||
                p.Description.Contains(query, StringComparison.OrdinalIgnoreCase)  ||
                p.Author.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (pricingFilter != "all")
            results = results.Where(p => p.PricingType == pricingFilter);

        var list = results.ToList();

        TxtSearchResultsLabel.Text = list.Count > 0
            ? $"RESULTADOS ({list.Count})"
            : "";

        PluginSearchResultsList.Children.Clear();
        foreach (var p in list)
            PluginSearchResultsList.Children.Add(BuildStoreCard(p));

        TxtSearchNoResults.Visibility = list.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CmbPluginFilter_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (TxtPluginSearch != null)
            TxtPluginSearch_TextChanged(s, null!);
    }

    private void ChkShowInstalled_Changed(object s, RoutedEventArgs e)
    {
        if (TxtPluginSearch != null)
            TxtPluginSearch_TextChanged(s, null!);
    }

    // ── My Plugins panel ──────────────────────────────────────────────────

    private void LoadMyPluginsPanel()
    {
        var manifests = FocusTracker.Plugins.PluginManager.Instance.GetAllManifests();
        MyPluginsListPanel.Children.Clear();

        if (manifests.Count == 0)
        {
            TxtNoMyPlugins.Visibility = Visibility.Visible;
            return;
        }

        TxtNoMyPlugins.Visibility = Visibility.Collapsed;
        foreach (var m in manifests)
        {
            bool disabled = FocusTracker.Plugins.PluginManager.Instance.IsDisabled(m.Id);
            MyPluginsListPanel.Children.Add(BuildMyPluginCard(m, disabled));
        }
    }

    private Border BuildMyPluginCard(FocusTracker.Plugins.PluginManifest m, bool isDisabled)
    {
        const double BTN_PADDING_H = 14;
        const double BTN_PADDING_V = 8;

        // ── Outer card ────────────────────────────────────────────────────
        var card = new Border
        {
            Background      = (System.Windows.Media.Brush)FindResource("BgCardBrush"),
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(22, 18, 22, 18),
            Margin          = new Thickness(0, 0, 0, 10),
            Opacity         = isDisabled ? 0.60 : 1.0
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Left: info + action buttons ───────────────────────────────────
        var info = new StackPanel();
        Grid.SetColumn(info, 0);

        // Title row
        var titleRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        titleRow.Children.Add(new TextBlock
        {
            Text              = m.Name,
            FontSize          = 15,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        // Status badge
        {
            var badge = new Border
            {
                Background      = (System.Windows.Media.Brush)FindResource("BgCardBrush"),
                BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(7, 3, 7, 3),
                Margin          = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text       = isDisabled ? "DESACTIVADO" : "ACTIVO",
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = isDisabled
                    ? (System.Windows.Media.Brush)FindResource("TextMutedBrush")
                    : (System.Windows.Media.Brush)FindResource("AccentBrush")
            };
            titleRow.Children.Add(badge);
        }
        info.Children.Add(titleRow);

        // ID · Author · Version
        info.Children.Add(new TextBlock
        {
            Text       = $"{m.Id}  ·  {m.Author}  ·  v{m.Version}",
            FontSize   = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
            Margin     = new Thickness(0, 0, 0, 7)
        });

        // Description
        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            info.Children.Add(new TextBlock
            {
                Text         = m.Description,
                FontSize     = 12,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 18,
                Margin       = new Thickness(0, 0, 0, 14)
            });
        }

        // ── Left action buttons: Desinstalar + Activar/Desactivar ─────────
        var leftActions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 0)
        };

        var btnUninstall = new WpfButton
        {
            Content    = "Desinstalar",
            Style      = (Style)FindResource("OutlineButton"),
            Padding    = new Thickness(BTN_PADDING_H, BTN_PADDING_V, BTN_PADDING_H, BTN_PADDING_V),
            Margin     = new Thickness(0, 0, 8, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush")
        };
        btnUninstall.Click += (_, _) => OnUninstallPlugin(m.Id, m.Name);
        leftActions.Children.Add(btnUninstall);

        var btnToggle = new WpfButton
        {
            Content = isDisabled ? "Activar" : "Desactivar",
            Style   = (Style)FindResource("OutlineButton"),
            Padding = new Thickness(BTN_PADDING_H, BTN_PADDING_V, BTN_PADDING_H, BTN_PADDING_V)
        };
        btnToggle.Click += (_, _) => OnTogglePlugin(m.Id, isDisabled);
        leftActions.Children.Add(btnToggle);

        info.Children.Add(leftActions);
        root.Children.Add(info);

        // ── Right: Ayuda button ───────────────────────────────────────────
        var helpContent = FocusTracker.Plugins.PluginManager.Instance.GetHelpContent(m.Id);
        var btnHelp = new WpfButton
        {
            Style             = (Style)FindResource("OutlineButton"),
            Padding           = new Thickness(BTN_PADDING_H, BTN_PADDING_V, BTN_PADDING_H, BTN_PADDING_V),
            Margin            = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled         = helpContent != null,
            ToolTip           = helpContent != null ? "Ver ayuda del plugin" : "Ayuda no disponible"
        };
        var helpContent2 = new StackPanel { Orientation = WpfOrientation.Horizontal };
        helpContent2.Children.Add(new TextBlock
        {
            Text              = "\uE9CE",
            FontFamily        = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize          = 13,
            Margin            = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        helpContent2.Children.Add(new TextBlock
        {
            Text              = "Ayuda",
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        btnHelp.Content = helpContent2;
        btnHelp.Click  += (_, _) => ShowPluginHelp(m.Id, m.Name);
        Grid.SetColumn(btnHelp, 1);
        root.Children.Add(btnHelp);

        card.Child = root;
        return card;
    }

    // ── Plugin Help panel ─────────────────────────────────────────────────

    private void LoadPluginHelpContent(string pluginId, string pluginName)
    {
        TxtHelpPluginName.Text = pluginName;
        PluginHelpContentPanel.Children.Clear();

        var help = FocusTracker.Plugins.PluginManager.Instance.GetHelpContent(pluginId);
        if (help == null)
        {
            PluginHelpContentPanel.Children.Add(new TextBlock
            {
                Text         = "No hay información de ayuda disponible para este plugin.",
                FontSize     = 13,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        // Summary block
        var summaryBorder = new Border
        {
            Background      = (System.Windows.Media.Brush)FindResource("BgCardBrush"),
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(20, 16, 20, 16),
            Margin          = new Thickness(0, 0, 0, 20)
        };
        summaryBorder.Child = new TextBlock
        {
            Text         = help.Summary,
            FontSize     = 13,
            Foreground   = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 20
        };
        PluginHelpContentPanel.Children.Add(summaryBorder);

        // Sections
        foreach (var section in help.Sections)
        {
            var sectionPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

            sectionPanel.Children.Add(new TextBlock
            {
                Text       = section.Heading,
                FontSize   = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                Margin     = new Thickness(0, 0, 0, 7)
            });

            sectionPanel.Children.Add(new TextBlock
            {
                Text         = section.Body,
                FontSize     = 13,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 20
            });

            PluginHelpContentPanel.Children.Add(sectionPanel);
        }
    }

    // ── Uninstall / toggle (shared) ────────────────────────────────────────

    private void OnUninstallPlugin(string id, string name)
    {
        bool confirmed = ConfirmDialog.Show(
            this,
            $"¿Desinstalar '{name}'?",
            "El plugin se eliminará permanentemente. Esta acción no se puede deshacer.",
            "Desinstalar", danger: true);
        if (!confirmed) return;

        FocusTracker.Plugins.PluginRegistry.Instance.UnregisterContributions(id);
        var error = FocusTracker.Plugins.PluginManager.Instance.Uninstall(id);
        if (error != null)
            WpfMessageBox.Show($"No se pudo desinstalar: {error}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

        LoadMyPluginsPanel();
        LoadPluginSettingCards();
        LoadHomePluginContent();
    }

    private void OnTogglePlugin(string id, bool wasDisabled)
    {
        if (wasDisabled)
            FocusTracker.Plugins.PluginManager.Instance.Enable(id);
        else
        {
            FocusTracker.Plugins.PluginRegistry.Instance.UnregisterContributions(id);
            FocusTracker.Plugins.PluginManager.Instance.Disable(id);
        }

        LoadMyPluginsPanel();
        LoadPluginSettingCards();
        LoadHomePluginContent();
    }

    private void BtnImportPlugin_Click(object s, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Seleccionar plugin de Focus Tracker",
            Filter = "Focus Tracker Plugin (*.focusplugin)|*.focusplugin",
            CheckFileExists = true
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var error = FocusTracker.Plugins.PluginManager.Instance.Install(dlg.FileName);
        if (error != null)
        {
            WpfMessageBox.Show(
                $"No se pudo instalar el plugin:\n\n{error}",
                "Error al instalar", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // After install, go to My Plugins to show the newly installed plugin
        ShowMyPlugins();
        LoadPluginSettingCards();
        LoadHomePluginContent();
        Views.ToastWindow.Show("Plugin instalado", $"El plugin se instaló correctamente.");
    }

    // ── Configuración ─────────────────────────────────────────────────────

    private void LoadSettingsPage()
    {
        var s = App.Settings;
        TxtDataFolder.Text      = s.DataFolder;
        _loadingSettings = true;
        ToggleSound.IsChecked      = s.NotificationSound;
        ToggleIdle.IsChecked       = s.IdleDetection;
        ToggleAutoUpdate.IsChecked = s.AutoUpdate;

        // Idle timeout input — suppress TextChanged while loading
        TxtIdleTimeout.Text = s.IdleTimeoutSeconds.ToString();
        _loadingSettings = false;
        UpdateIdleDescription(s.IdleTimeoutSeconds);

        // Sync idle detection to the running tracker
        App.Tracker.IdleDetectionEnabled = s.IdleDetection;
        App.Tracker.IdleTimeoutSeconds   = s.IdleTimeoutSeconds;

        // Build unified tab bar (GENERAL + plugin tabs)
        RefreshSettingsTabs();
    }

    private bool _loadingSettings = false;

    private void UpdateIdleDescription(int seconds)
    {
        TxtIdleDescription.Text =
            $"Si no hay input de teclado o mouse por más de {seconds} segundos, " +
            "el tracking se pausa automáticamente. Podés reanudarlo desde la notificación o el botón en la barra lateral.";
    }

    private void TxtIdleTimeout_TextChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (!int.TryParse(TxtIdleTimeout.Text, out int secs) || secs < 5) return;
        App.Settings.IdleTimeoutSeconds  = secs;
        App.Settings.Save();
        App.Tracker.IdleTimeoutSeconds   = secs;
        UpdateIdleDescription(secs);
    }

    // ── Settings tab system ───────────────────────────────────────────────────
    private const string SettingsTabGeneral = "GENERAL";
    private string _activeSettingsTab = SettingsTabGeneral;

    /// <summary>
    /// Rebuilds the tab bar (SettingsTabBar) and shows/hides content panes.
    /// "GENERAL" tab shows SettingsGeneralContent; plugin tabs populate PluginSettingsContainer.
    /// </summary>
    private void RefreshSettingsTabs()
    {
        var registry  = FocusTracker.Plugins.PluginRegistry.Instance;
        var pluginTabs = registry.GetSettingTabNames(); // e.g. ["Pomodoro"]

        // Build full list: GENERAL first, then plugin tabs
        var allTabs = new List<string> { SettingsTabGeneral };
        allTabs.AddRange(pluginTabs);

        // Guard: if active tab was removed, fall back to GENERAL
        if (!allTabs.Contains(_activeSettingsTab, StringComparer.OrdinalIgnoreCase))
            _activeSettingsTab = SettingsTabGeneral;

        // ── Rebuild tab bar ──────────────────────────────────────────────────
        SettingsTabBar.Children.Clear();

        foreach (var tabName in allTabs)
        {
            var name     = tabName;
            var isActive = string.Equals(name, _activeSettingsTab, StringComparison.OrdinalIgnoreCase);
            var tabBtn   = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content   = name,
                IsChecked = isActive,
                Style     = (Style)FindResource("TabButton"),
                Margin    = new Thickness(0, 0, 6, 0),
            };
            tabBtn.Click += (_, _) =>
            {
                _activeSettingsTab = name;
                RefreshSettingsTabs();
            };
            SettingsTabBar.Children.Add(tabBtn);
        }

        // ── Show/hide content panes ──────────────────────────────────────────
        bool isGeneral = string.Equals(_activeSettingsTab, SettingsTabGeneral, StringComparison.OrdinalIgnoreCase);

        SettingsGeneralContent.Visibility  = isGeneral ? Visibility.Visible : Visibility.Collapsed;
        PluginSettingsContainer.Visibility = isGeneral ? Visibility.Collapsed : Visibility.Visible;

        if (!isGeneral)
        {
            // Populate plugin content for the selected tab
            PluginSettingsContainer.Children.Clear();
            foreach (var d in registry.GetSettingsForTab(_activeSettingsTab))
            {
                var row = BuildPluginSettingRow(d);
                if (row != null) PluginSettingsContainer.Children.Add(row);
            }
        }
    }

    // Kept as alias so existing call sites still compile
    private void LoadPluginSettingCards() => RefreshSettingsTabs();

    // ── Plugin button renderer (shared across screens) ────────────────────────

    /// <summary>
    /// Builds a horizontal row of buttons contributed by plugins for the given screen.
    /// Returns null when there are no contributions for that screen.
    /// </summary>
    private FrameworkElement? BuildPluginButtonRow(FocusTracker.Plugins.PluginScreenTarget screen)
    {
        var buttons = FocusTracker.Plugins.PluginRegistry.Instance.GetButtonsForScreen(screen);
        if (buttons.Count == 0) return null;

        var row = new WrapPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin      = new Thickness(0, 12, 0, 0),
        };

        foreach (var btn in buttons)
        {
            row.Children.Add(BuildPluginButton(btn));
        }
        return row;
    }

    private WpfButton BuildPluginButton(FocusTracker.Plugins.PluginButtonContribution btn)
    {
        var styleKey = btn.Style switch
        {
            FocusTracker.Plugins.PluginButtonStyle.Primary   => "PrimaryButton",
            FocusTracker.Plugins.PluginButtonStyle.Danger    => "DangerButton",
            FocusTracker.Plugins.PluginButtonStyle.Secondary => "SecondaryButton",
            FocusTracker.Plugins.PluginButtonStyle.Tertiary  => "TertiaryButton",
            _                                                => "OutlineButton",
        };

        FrameworkElement content;
        if (!string.IsNullOrEmpty(btn.Icon))
        {
            var iconText = new TextBlock
            {
                Text       = btn.Icon,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize   = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 7, 0),
            };
            // Icon inherits button foreground via binding
            var sp = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
            };
            sp.Children.Add(iconText);
            sp.Children.Add(new TextBlock
            {
                Text              = btn.Label,
                VerticalAlignment = VerticalAlignment.Center,
            });
            content = sp;
        }
        else
        {
            content = new TextBlock { Text = btn.Label };
        }

        var b = new WpfButton
        {
            Content = content,
            Style   = (Style)FindResource(styleKey),
            Padding = new Thickness(16, 10, 16, 10),
            Margin  = new Thickness(0, 0, 10, 6),
            Tag     = btn,   // stored so RefreshPluginButtonVisibility can find it
        };
        b.Click += (_, _) =>
        {
            try { btn.OnClicked(); }
            catch { /* plugin fault — swallow so host stays stable */ }
        };
        return b;
    }

    // ── Plugin home cards renderer ────────────────────────────────────────────

    // Timers for auto-refreshing plugin home cards
    private readonly List<System.Windows.Threading.DispatcherTimer> _homeCardTimers = new();

    private void LoadPluginHomeCards(System.Windows.Controls.Panel container)
    {
        // Stop any previous timers
        foreach (var t in _homeCardTimers) t.Stop();
        _homeCardTimers.Clear();

        var cards = FocusTracker.Plugins.PluginRegistry.Instance.GetHomeCards();
        foreach (var card in cards)
        {
            var c = card; // capture
            var cardBorder = BuildPluginHomeCard(c);
            container.Children.Add(cardBorder);

            if (c.AutoRefreshSeconds > 0)
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(c.AutoRefreshSeconds)
                };
                timer.Tick += (_, _) => RefreshPluginHomeCard(cardBorder, c);
                timer.Start();
                _homeCardTimers.Add(timer);
            }
        }
    }

    private Border BuildPluginHomeCard(FocusTracker.Plugins.PluginCardContribution card)
    {
        var border = new Border
        {
            Background      = (System.Windows.Media.Brush)FindResource("BgCardBrush"),
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(24, 20, 24, 20),
            Margin          = new Thickness(0, 0, 0, 16),
        };

        // If the plugin registered a click handler, make the card interactive
        if (card.OnCardClicked != null)
        {
            border.Cursor = System.Windows.Input.Cursors.Hand;
            border.MouseLeftButtonUp += (_, _) =>
            {
                try { card.OnCardClicked?.Invoke(); } catch { }
            };
            border.MouseEnter += (_, _) =>
                border.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            border.MouseLeave += (_, _) =>
                border.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        }

        RefreshPluginHomeCard(border, card);
        return border;
    }

    private void RefreshPluginHomeCard(Border border, FocusTracker.Plugins.PluginCardContribution card)
    {
        IReadOnlyList<FocusTracker.Plugins.PluginCardRow> rows;
        try { rows = card.GetRows(); }
        catch { rows = Array.Empty<FocusTracker.Plugins.PluginCardRow>(); }

        var stack = new StackPanel();

        // Header
        var headerRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        if (!string.IsNullOrEmpty(card.Icon))
        {
            headerRow.Children.Add(new TextBlock
            {
                Text              = card.Icon,
                FontFamily        = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize          = 16,
                Foreground        = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0),
            });
        }
        headerRow.Children.Add(new TextBlock
        {
            Text       = card.Title,
            FontSize   = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(headerRow);

        // Rows
        if (rows.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text       = "Sin datos disponibles.",
                FontSize   = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
            });
        }
        else
        {
            foreach (var row in rows)
            {
                if (row.IsHeader)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text       = row.Label.ToUpperInvariant(),
                        FontSize   = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                        Margin     = new Thickness(0, 10, 0, 4),
                    });
                }
                else
                {
                    var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    g.Children.Add(new TextBlock
                    {
                        Text       = row.Label,
                        FontSize   = 13,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    });

                    if (!string.IsNullOrEmpty(row.Value))
                    {
                        var valTb = new TextBlock
                        {
                            Text       = row.Value,
                            FontSize   = 13,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        };
                        Grid.SetColumn(valTb, 1);
                        g.Children.Add(valTb);
                    }

                    stack.Children.Add(g);
                }
            }
        }

        border.Child = stack;
    }

    private UIElement? BuildPluginSettingRow(FocusTracker.Plugins.PluginSettingDescriptor d)
        => d.Type switch
        {
            FocusTracker.Plugins.PluginSettingType.Toggle       => BuildPsToggle(d),
            FocusTracker.Plugins.PluginSettingType.TextInput    => BuildPsTextInput(d),
            FocusTracker.Plugins.PluginSettingType.Button       => BuildPsButton(d),
            FocusTracker.Plugins.PluginSettingType.Select       => BuildPsSelect(d),
            FocusTracker.Plugins.PluginSettingType.AutoComplete => BuildPsAutoComplete(d),
            FocusTracker.Plugins.PluginSettingType.FilePicker   => BuildPsFilePicker(d),
            FocusTracker.Plugins.PluginSettingType.FolderPicker => BuildPsFolderPicker(d),
            FocusTracker.Plugins.PluginSettingType.Label        => BuildPsLabel(d),
            _ => null,
        };

    // ── Label (section header / divider) ──────────────────────────────────────
    private UIElement BuildPsLabel(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var sp = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 4),
        };

        // Bold section title
        sp.Children.Add(new TextBlock
        {
            Text         = d.Title,
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 2),
        });

        // Optional description
        if (!string.IsNullOrWhiteSpace(d.Description))
            sp.Children.Add(new TextBlock
            {
                Text         = d.Description,
                FontSize     = 11,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.7,
            });

        // Thin separator line underneath
        sp.Children.Add(new Border
        {
            Height          = 1,
            Background      = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            Margin          = new Thickness(0, 6, 0, 0),
            Opacity         = 0.5,
        });

        return sp;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private Border MakePsCard(bool isDanger = false)
    {
        System.Windows.Media.Brush bg = isDanger
            ? new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0x1A, 0x08, 0x08))
            : (System.Windows.Media.Brush)FindResource("BgCardBrush");
        System.Windows.Media.Brush border = isDanger
            ? new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0x3A, 0x10, 0x10))
            : (System.Windows.Media.Brush)FindResource("BorderBrush");

        return new Border
        {
            Background      = bg,
            BorderBrush     = border,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(24, 20, 24, 20),
            Margin          = new Thickness(0, 0, 0, 12),
        };
    }

    private StackPanel MakePsLabels(FocusTracker.Plugins.PluginSettingDescriptor d, bool isDanger = false)
    {
        var primary = isDanger
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text         = d.Title,
            FontSize     = 15,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = primary,
            Margin       = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(d.Description))
            sp.Children.Add(new TextBlock
            {
                Text         = d.Description,
                FontSize     = 12,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        return sp;
    }

    private static Grid MakePsTwoColGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    // ── Toggle ────────────────────────────────────────────────────────────────

    private Border BuildPsToggle(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard();
        var grid   = MakePsTwoColGrid();
        var labels = MakePsLabels(d);
        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var toggle = new ToggleButton
        {
            Style             = (Style)FindResource("ToggleSwitchStyle"),
            IsChecked         = d.ToggleDefault,
            Margin            = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Checked   += (_, _) => d.OnToggleChanged?.Invoke(true);
        toggle.Unchecked += (_, _) => d.OnToggleChanged?.Invoke(false);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        card.Child = grid;
        return card;
    }

    // ── TextInput ─────────────────────────────────────────────────────────────

    private Border BuildPsTextInput(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard();
        var labels = MakePsLabels(d);

        var box = new WpfTextBox
        {
            Text            = d.TextDefault ?? "",
            FontSize        = 13,
            Padding         = new Thickness(10, 8, 10, 8),
            Background      = System.Windows.Media.Brushes.Transparent,
            Foreground      = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var boxBorder = new Border
        {
            Background      = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0x0F, 0x0F, 0x15)),
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Margin          = new Thickness(0, 10, 0, 0),
            Child           = box,
        };

        void Commit() => d.OnTextCommitted?.Invoke(box.Text);
        box.LostFocus += (_, _) => Commit();
        box.KeyDown   += (_, e) => { if (e.Key == Key.Return) Commit(); };

        labels.Children.Add(boxBorder);
        card.Child = labels;
        return card;
    }

    // ── Button ────────────────────────────────────────────────────────────────

    private Border BuildPsButton(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard(d.ButtonIsDanger);
        var grid   = MakePsTwoColGrid();
        var labels = MakePsLabels(d, d.ButtonIsDanger);
        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var psButtonStyle = d.ButtonIsDanger    ? "DangerButton"
                          : d.ButtonIsSecondary ? "SecondaryButton"
                          : "OutlineButton";
        var btn = new WpfButton
        {
            Content           = d.ButtonLabel ?? d.Title,
            Style             = (Style)FindResource(psButtonStyle),
            Padding           = new Thickness(16, 10, 16, 10),
            Margin            = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => d.OnButtonClicked?.Invoke();
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);

        card.Child = grid;
        return card;
    }

    // ── Select ────────────────────────────────────────────────────────────────

    private Border BuildPsSelect(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard();
        var grid   = MakePsTwoColGrid();
        var labels = MakePsLabels(d);
        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var combo = new WpfComboBox
        {
            MinWidth          = 160,
            Margin            = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style             = (Style)FindResource("PluginComboBoxStyle"),
        };
        foreach (var opt in d.SelectOptions ?? Array.Empty<string>())
            combo.Items.Add(opt);
        if (d.SelectOptions != null && d.SelectDefaultIndex < d.SelectOptions.Length)
            combo.SelectedIndex = d.SelectDefaultIndex;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string sel) d.OnSelectChanged?.Invoke(sel);
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        card.Child = grid;
        return card;
    }

    // ── AutoComplete ──────────────────────────────────────────────────────────

    private Border BuildPsAutoComplete(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard();
        var labels = MakePsLabels(d);

        var mutedBrush   = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        var primaryBrush = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        var bgDark       = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0x0F, 0x0F, 0x15));

        // ── Text box ──────────────────────────────────────────────────────────
        var box = new WpfTextBox
        {
            Text              = d.TextDefault ?? "",
            FontSize          = 13,
            Padding           = new Thickness(12, 10, 12, 10),
            Background        = System.Windows.Media.Brushes.Transparent,
            Foreground        = primaryBrush,
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Placeholder simulation
        bool isShowingPlaceholder = false;
        if (!string.IsNullOrEmpty(d.TextPlaceholder) && string.IsNullOrWhiteSpace(box.Text))
        {
            box.Text       = d.TextPlaceholder;
            box.Foreground = mutedBrush;
            isShowingPlaceholder = true;
        }

        var boxBorder = new Border
        {
            Background      = bgDark,
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Margin          = new Thickness(0, 10, 0, 0),
            Child           = box,
        };

        // ── Suggestion list inside a Popup ────────────────────────────────────
        var itemBg     = (System.Windows.Media.Brush)FindResource("BgCardBrush");
        var itemHover  = (System.Windows.Media.Brush)FindResource("BgCardHoverBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");

        var listPanel = new StackPanel();

        var popup = new Popup
        {
            PlacementTarget    = box,
            Placement          = PlacementMode.Bottom,
            StaysOpen          = true,   // we control close manually to allow click
            AllowsTransparency = true,
            PopupAnimation     = PopupAnimation.None,
            Child              = new Border
            {
                Background      = itemBg,
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(0, 0, 8, 8),
                Margin          = new Thickness(0, -1, 0, 0),
                Child           = new ScrollViewer
                {
                    MaxHeight                   = 230,
                    Content                     = listPanel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            },
        };

        // ── Helpers ────────────────────────────────────────────────────────────
        void CommitValue(string value)
        {
            popup.IsOpen     = false;
            isShowingPlaceholder = false;
            box.Text         = value;
            box.Foreground   = primaryBrush;
            box.CaretIndex   = value.Length;
            try { d.OnAutoCompleteCommitted?.Invoke(value); } catch { }
        }

        void UpdatePopup(string query)
        {
            if (d.AutoCompleteGetSuggestions == null || string.IsNullOrWhiteSpace(query))
            {
                popup.IsOpen = false;
                return;
            }

            IReadOnlyList<string> items;
            try { items = d.AutoCompleteGetSuggestions(query); }
            catch { popup.IsOpen = false; return; }
            if (items == null || items.Count == 0) { popup.IsOpen = false; return; }

            listPanel.Children.Clear();
            foreach (var item in items)
            {
                var row = item; // capture
                var li  = new Border
                {
                    Padding    = new Thickness(14, 9, 14, 9),
                    Background = System.Windows.Media.Brushes.Transparent,
                    Cursor     = WpfCursors.Hand,
                    Child      = new TextBlock
                    {
                        Text       = row,
                        FontSize   = 13,
                        Foreground = primaryBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                li.MouseEnter       += (_, _) => li.Background = itemHover;
                li.MouseLeave       += (_, _) => li.Background = System.Windows.Media.Brushes.Transparent;
                li.MouseLeftButtonUp += (_, e) => { CommitValue(row); e.Handled = true; };
                listPanel.Children.Add(li);
            }

            // Sync popup width to the border (parent of the text box)
            boxBorder.UpdateLayout();
            popup.Width  = boxBorder.ActualWidth > 0 ? boxBorder.ActualWidth : 300;
            popup.IsOpen = true;
        }

        // ── Events ─────────────────────────────────────────────────────────────
        box.GotFocus += (_, _) =>
        {
            if (isShowingPlaceholder)
            {
                isShowingPlaceholder = false;
                box.Text       = "";
                box.Foreground = primaryBrush;
            }
            // highlight the border
            boxBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        };

        box.LostFocus += (_, _) =>
        {
            boxBorder.BorderBrush = borderBrush;
            if (string.IsNullOrWhiteSpace(box.Text) && !string.IsNullOrEmpty(d.TextPlaceholder))
            {
                isShowingPlaceholder = true;
                box.Text       = d.TextPlaceholder;
                box.Foreground = mutedBrush;
            }
            // Close popup with a tiny delay so mouse-clicks on items can register
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                (Action)(() => { if (!listPanel.IsMouseOver) popup.IsOpen = false; }));
        };

        box.TextChanged += (_, _) =>
        {
            if (isShowingPlaceholder) return;
            UpdatePopup(box.Text);
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                var val = isShowingPlaceholder ? "" : box.Text;
                CommitValue(val);
                e.Handled = true;
                return;
            }
            if (!popup.IsOpen) return;
            var count = listPanel.Children.Count;
            if (count == 0) return;
            // ↓/↑ keyboard navigation — highlight a row
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                int cur = -1;
                for (int i = 0; i < count; i++)
                    if (listPanel.Children[i] is Border b &&
                        b.Background == itemHover) { cur = i; break; }

                for (int i = 0; i < count; i++)
                    if (listPanel.Children[i] is Border b2) b2.Background = System.Windows.Media.Brushes.Transparent;

                int next = e.Key == Key.Down
                    ? Math.Min(count - 1, cur + 1)
                    : Math.Max(0, cur == -1 ? 0 : cur - 1);
                if (listPanel.Children[next] is Border target)
                    target.Background = itemHover;
                e.Handled = true;
            }
        };

        labels.Children.Add(boxBorder);
        // Add popup to the card so it's associated with this part of the visual tree
        labels.Children.Add(popup);
        card.Child = labels;
        return card;
    }

    // ── FilePicker ────────────────────────────────────────────────────────────

    private Border BuildPsFilePicker(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard();
        var labels = MakePsLabels(d);

        var pathBox = new WpfTextBox
        {
            Text            = d.PathDefault ?? "",
            FontSize        = 12,
            Padding         = new Thickness(10, 8, 10, 8),
            Background      = System.Windows.Media.Brushes.Transparent,
            Foreground      = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            BorderThickness = new Thickness(0),
            IsReadOnly      = true,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily      = new System.Windows.Media.FontFamily("Consolas"),
        };

        var browseBtn = new WpfButton
        {
            Content           = "Examinar…",
            Style             = (Style)FindResource("OutlineButton"),
            Padding           = new Thickness(12, 8, 12, 8),
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = d.FileFilter ?? "Todos los archivos|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                pathBox.Text = dlg.FileName;
                d.OnPathSelected?.Invoke(dlg.FileName);
            }
        };

        var rowGrid = MakePsTwoColGrid();
        var pathBorder = new Border
        {
            Background      = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0x0F, 0x0F, 0x15)),
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Child           = pathBox,
        };
        Grid.SetColumn(pathBorder, 0);
        Grid.SetColumn(browseBtn, 1);
        rowGrid.Children.Add(pathBorder);
        rowGrid.Children.Add(browseBtn);

        labels.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = rowGrid });
        card.Child = labels;
        return card;
    }

    // ── FolderPicker ──────────────────────────────────────────────────────────

    private Border BuildPsFolderPicker(FocusTracker.Plugins.PluginSettingDescriptor d)
    {
        var card   = MakePsCard();
        var labels = MakePsLabels(d);

        var pathBox = new WpfTextBox
        {
            Text            = d.PathDefault ?? "",
            FontSize        = 12,
            Padding         = new Thickness(10, 8, 10, 8),
            Background      = System.Windows.Media.Brushes.Transparent,
            Foreground      = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            BorderThickness = new Thickness(0),
            IsReadOnly      = true,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily      = new System.Windows.Media.FontFamily("Consolas"),
        };

        var browseBtn = new WpfButton
        {
            Content           = "Examinar…",
            Style             = (Style)FindResource("OutlineButton"),
            Padding           = new Thickness(12, 8, 12, 8),
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                SelectedPath = pathBox.Text,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                pathBox.Text = dlg.SelectedPath;
                d.OnPathSelected?.Invoke(dlg.SelectedPath);
            }
        };

        var rowGrid   = MakePsTwoColGrid();
        var pathBorder = new Border
        {
            Background      = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0x0F, 0x0F, 0x15)),
            BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Child           = pathBox,
        };
        Grid.SetColumn(pathBorder, 0);
        Grid.SetColumn(browseBtn, 1);
        rowGrid.Children.Add(pathBorder);
        rowGrid.Children.Add(browseBtn);

        labels.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = rowGrid });
        card.Child = labels;
        return card;
    }

    private void BtnBrowseFolder_Click(object s, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Seleccioná la carpeta donde se guardarán los datos de Focus Tracker",
            UseDescriptionForTitle = true,
            SelectedPath        = App.Settings.DataFolder
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var newFolder = dialog.SelectedPath;
        var oldFolder = App.Settings.DataFolder;

        if (string.Equals(newFolder, oldFolder, StringComparison.OrdinalIgnoreCase)) return;

        // Confirm with user
        if (!ConfirmDialog.Show(this,
                "Cambiar carpeta de datos",
                $"Los datos se moverán de:\n{oldFolder}\n\na:\n{newFolder}\n\n¿Continuar?",
                "Cambiar y mover")) return;

        try
        {
            MoveDataFolder(oldFolder, newFolder);
            App.Settings.DataFolder = newFolder;
            App.Settings.Save();
            TxtDataFolder.Text = newFolder;
            ToastWindow.Show("Carpeta actualizada", "Los datos se movieron. Reiniciá la app para aplicar el cambio.");
        }
        catch (Exception ex)
        {
            ToastWindow.Show("Error", $"No se pudo mover los datos: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>
    /// Moves focustracker.db (and crash.log if present) from one folder to another.
    /// The app must restart to use the new DB; for now we just update settings so
    /// the new path is used on next launch. We also hot-swap the connection if safe.
    /// </summary>
    private static void MoveDataFolder(string fromDir, string toDir)
    {
        Directory.CreateDirectory(toDir);

        var files = new[] { "focustracker.db", "focustracker.db-wal", "focustracker.db-shm", "crash.log" };
        foreach (var file in files)
        {
            var src = Path.Combine(fromDir, file);
            var dst = Path.Combine(toDir, file);
            if (File.Exists(src))
                File.Move(src, dst, overwrite: true);
        }
    }

    private void ToggleSound_Changed(object s, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        App.Settings.NotificationSound = ToggleSound.IsChecked == true;
        App.Settings.Save();
    }

    private void ToggleIdle_Changed(object s, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool enabled = ToggleIdle.IsChecked == true;
        App.Settings.IdleDetection = enabled;
        App.Settings.Save();
        App.Tracker.IdleDetectionEnabled  = enabled;
        App.Tracker.IdleTimeoutSeconds    = App.Settings.IdleTimeoutSeconds;
    }

    private void BtnResetData_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Show(this,
                "Restablecer todos los datos",
                "Se eliminarán permanentemente todas las sesiones, proyectos y eventos registrados. Esta acción no se puede deshacer.",
                "Eliminar todo", danger: true)) return;

        App.Database.ResetDatabase();
        _projects = new();
        RefreshHomeRecentProjects();
        ToastWindow.Show("Datos eliminados", "Todos los registros de tracking fueron borrados.", ToastKind.Warning);
    }

    /// <summary>Called on the UI thread when TrackingService detects idle > 30 s.</summary>
    private void OnIdleDetected()
    {
        if (!App.Tracker.IsTracking || App.Tracker.IsPaused) return;

        // Pause (don't stop) — session stays open in DB
        App.Tracker.PauseTracking();

        // Update sidebar status to show paused state
        StatusDot.Fill      = new WpfBrush(WpfColor.FromRgb(0xFF, 0xAA, 0x44)); // orange
        TxtStatusLabel.Text = "PAUSADO";
        TxtCurrentApp.Text  = "Inactividad detectada";

        // Show resume buttons
        BtnSidebarResume.Visibility = Visibility.Visible;
        BtnLiveResume.Visibility    = Visibility.Visible;

        // Persistent notification with Resume action
        ToastWindow.ShowPersistent(
            "Tracking pausado",
            "No se detectó actividad por más de 30 segundos.",
            ToastKind.Info,
            new[]
            {
                new FocusTracker.Plugins.PluginToastAction
                {
                    Label     = "▶  Reanudar",
                    OnClicked = ResumeTracking,
                },
            });
    }

    private void ResumeTracking()
    {
        if (!App.Tracker.IsTracking || !App.Tracker.IsPaused) return;

        App.Tracker.ResumeTracking();

        // Restore active status UI
        StatusDot.Fill      = (WpfBrush)FindResource("AccentBrush");
        TxtStatusLabel.Text = "ACTIVO";

        // Hide resume buttons
        BtnSidebarResume.Visibility = Visibility.Collapsed;
        BtnLiveResume.Visibility    = Visibility.Collapsed;
    }

    private void BtnResume_Click(object s, RoutedEventArgs e) => ResumeTracking();

    // ══════════════════════════════════════════════════════════════════════════
    // ── PLUGIN OPERATION HELPERS ──────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the exact manifest Id (as stored in plugin.json) from a store API id.
    /// The store API may return an id with different casing than what is in plugin.json,
    /// and PluginManager._manifests uses the default case-sensitive comparer.
    /// Steps:
    ///   1. Check loaded manifests (fast, OrdinalIgnoreCase).
    ///   2. Scan .focusplugin ZIPs on disk and read plugin.json from each match.
    /// Falls back to returning storeId unchanged if nothing matches.
    /// </summary>
    private string ResolvePluginId(string storeId)
    {
        // 1. Check loaded manifests first (fast, covers running plugins)
        var match = FocusTracker.Plugins.PluginManager.Instance
            .GetAllManifests()
            .FirstOrDefault(m => string.Equals(m.Id, storeId, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.Id;

        // 2. Scan disk — open each ZIP whose filename looks like this plugin
        var folder = FocusTracker.Plugins.PluginManager.Instance.PluginsFolder;
        if (!System.IO.Directory.Exists(folder)) return storeId;

        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.focusplugin"))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(file);
            bool nameMatch = string.Equals(stem, storeId, StringComparison.OrdinalIgnoreCase)
                          || stem.StartsWith(storeId + "-", StringComparison.OrdinalIgnoreCase);
            if (!nameMatch) continue;
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(file);
                var entry = zip.GetEntry("plugin.json");
                if (entry == null) continue;
                using var rdr = new System.IO.StreamReader(entry.Open());
                var m = FocusTracker.Plugins.PluginManifest.TryLoad(rdr.ReadToEnd());
                if (!string.IsNullOrEmpty(m?.Id)) return m!.Id;
            }
            catch { }
        }
        return storeId;
    }

    /// <summary>
    /// Deletes any .focusplugin file in the plugins folder whose filename stem
    /// matches "{pluginId}" or starts with "{pluginId}-".
    /// Called as a safety net after PluginManager.Uninstall() in case FindPluginFile
    /// (which reads the ZIP manifest) returned null for any reason.
    /// </summary>
    private void ForceDeletePluginFile(string pluginId)
    {
        var folder = FocusTracker.Plugins.PluginManager.Instance.PluginsFolder;
        if (!System.IO.Directory.Exists(folder)) return;

        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.focusplugin"))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(file);
            bool match = string.Equals(stem, pluginId, StringComparison.OrdinalIgnoreCase)
                      || stem.StartsWith(pluginId + "-", StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                try { System.IO.File.Delete(file); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Full plugin UI refresh — called after install/uninstall/enable/disable
    /// to ensure every screen that shows plugin content is up to date.
    /// </summary>
    private void RefreshAllPluginUI()
    {
        LoadMyPluginsPanel();
        LoadPluginSettingCards();
        LoadHomePluginContent();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ── PLUGIN DETAIL PANEL ───────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private void ShowPluginDetail(StorePluginDto p)
    {
        _detailPlugin = p;

        PluginStorePanel.Visibility     = Visibility.Collapsed;
        PluginMyPluginsPanel.Visibility = Visibility.Collapsed;
        PluginHelpPanel.Visibility      = Visibility.Collapsed;
        PluginDetailPanel.Visibility    = Visibility.Visible;

        TxtPluginDetailName.Text     = p.Name;
        TxtPluginDetailSubtitle.Text = $"por {p.Author}  ·  v{p.Version}";

        BuildDetailContent(p);
    }

    private void BtnDetailBack_Click(object s, RoutedEventArgs e)
    {
        _detailPlugin = null;
        PluginDetailPanel.Visibility = Visibility.Collapsed;
        ShowPluginStore();
    }

    private void BuildDetailContent(StorePluginDto p)
    {
        PluginDetailContentPanel.Children.Clear();

        bool isInstalled    = IsPluginInstalled(p.Id);
        var  resolvedDetailId = isInstalled ? ResolvePluginId(p.Id) : p.Id;
        bool isDisabled     = isInstalled && FocusTracker.Plugins.PluginManager.Instance.IsDisabled(resolvedDetailId);
        bool isInstalling   = _installingIds.Contains(p.Id);

        // ── Pricing / meta badges row ─────────────────────────────────────
        var badgeRow = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 20)
        };

        // Category badge
        if (!string.IsNullOrEmpty(p.CategoryName))
            badgeRow.Children.Add(MakeBadge(p.CategoryName, "BgCardBrush", "TextSecondaryBrush"));

        // Pricing badge
        var priceLabel = PricingBadgeLabel(p);
        if (!string.IsNullOrEmpty(priceLabel))
            badgeRow.Children.Add(MakeBadge(priceLabel, "AccentBrush", "BgDarkBrush"));

        // Download count badge
        var dlText = p.DownloadCount >= 1000
            ? $"{p.DownloadCount / 1000.0:0.#}k descargas"
            : $"{p.DownloadCount} descargas";
        badgeRow.Children.Add(MakeBadge(dlText, "BgCardBrush", "TextMutedBrush"));

        if (badgeRow.Children.Count > 0)
            PluginDetailContentPanel.Children.Add(badgeRow);

        // ── Description ───────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            PluginDetailContentPanel.Children.Add(new TextBlock
            {
                Text         = p.Description,
                FontSize     = 13,
                Foreground   = Br("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 20,
                Margin       = new Thickness(0, 0, 0, 28)
            });
        }

        // ── Action area ───────────────────────────────────────────────────
        var actionRow = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 24)
        };

        if (isInstalling)
        {
            var pb = new System.Windows.Controls.ProgressBar
            {
                IsIndeterminate = true,
                Width = 160, Height = 6,
                Foreground = Br("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            actionRow.Children.Add(pb);
            actionRow.Children.Add(new TextBlock
            {
                Text = "Instalando…",
                FontSize = 12,
                Foreground = Br("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else if (isInstalled)
        {
            var btnToggle = new WpfButton
            {
                Content = isDisabled ? "Activar" : "Desactivar",
                Style   = (Style)FindResource(isDisabled ? "PrimaryButton" : "OutlineButton"),
                Margin  = new Thickness(0, 0, 10, 0)
            };
            btnToggle.Click += (_, _) =>
            {
                var resolvedId = ResolvePluginId(p.Id);
                string? err;
                if (FocusTracker.Plugins.PluginManager.Instance.IsDisabled(resolvedId))
                {
                    err = FocusTracker.Plugins.PluginManager.Instance.Enable(resolvedId);
                }
                else
                {
                    FocusTracker.Plugins.PluginRegistry.Instance.UnregisterContributions(resolvedId);
                    FocusTracker.Plugins.PluginManager.Instance.Disable(resolvedId);
                    err = null;
                }
                if (err != null) ShowToast($"Error: {err}", isError: true);
                LoadMyPluginsPanel();
                LoadPluginSettingCards();
                LoadHomePluginContent();
                BuildDetailContent(p);
                RenderStoreList(_storePluginsData);
            };
            actionRow.Children.Add(btnToggle);

            var btnUninstall = new WpfButton
            {
                Content = "Desinstalar",
                Style   = (Style)FindResource("DangerButton")
            };
            btnUninstall.Click += (_, _) =>
            {
                bool confirmed = ConfirmDialog.Show(
                    this,
                    $"¿Desinstalar \"{p.Name}\"?",
                    "El plugin se eliminará permanentemente. Esta acción no se puede deshacer.",
                    "Desinstalar", danger: true);
                if (!confirmed) return;
                var resolvedId = ResolvePluginId(p.Id);
                FocusTracker.Plugins.PluginRegistry.Instance.UnregisterContributions(resolvedId);
                var err = FocusTracker.Plugins.PluginManager.Instance.Uninstall(resolvedId);
                if (err != null) { ShowToast($"Error: {err}", isError: true); return; }
                ForceDeletePluginFile(resolvedId);
                LoadMyPluginsPanel();
                LoadPluginSettingCards();
                LoadHomePluginContent();
                ShowToast($"\"{p.Name}\" desinstalado.");
                BtnDetailBack_Click(null!, null!);
            };
            actionRow.Children.Add(btnUninstall);
        }
        else
        {
            var btnGet = new WpfButton
            {
                Content = "Obtener",
                Style   = (Style)FindResource("PrimaryButton"),
                Margin  = new Thickness(0, 0, 10, 0)
            };
            btnGet.Click += (_, _) => OpenPaymentModal(p);
            actionRow.Children.Add(btnGet);
        }

        // "Ver ayuda" — only if installed AND the plugin has help content available
        bool hasHelp = FocusTracker.Plugins.PluginManager.Instance.GetHelpContent(resolvedDetailId) != null;
        if (isInstalled && hasHelp)
        {
            var btnHelp = new WpfButton
            {
                Content = "Ver ayuda",
                Style   = (Style)FindResource("GhostButton"),
                Margin  = new Thickness(10, 0, 0, 0)
            };
            btnHelp.Click += (_, _) => ShowPluginHelp(p.Id, p.Name);
            actionRow.Children.Add(btnHelp);
        }

        PluginDetailContentPanel.Children.Add(actionRow);

        // ── Info table (version, id) ──────────────────────────────────────
        var infoCard = new Border
        {
            Background      = Br("BgCardBrush"),
            BorderBrush     = Br("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(20, 16, 20, 16),
            Margin          = new Thickness(0, 0, 0, 0)
        };
        var infoStack = new StackPanel();
        infoStack.Children.Add(MakeInfoRow("Versión",    p.Version));
        infoStack.Children.Add(MakeInfoRow("ID",         p.Id));
        infoStack.Children.Add(MakeInfoRow("Autor",      p.Author));
        if (!string.IsNullOrEmpty(p.CategoryName))
            infoStack.Children.Add(MakeInfoRow("Categoría", p.CategoryName));
        infoCard.Child = infoStack;
        PluginDetailContentPanel.Children.Add(infoCard);
    }

    // ── Small helpers ──────────────────────────────────────────────────────────

    private Border MakeBadge(string text, string bgKey, string fgKey)
    {
        var b = new Border
        {
            Background      = Br(bgKey),
            CornerRadius    = new CornerRadius(5),
            Padding         = new Thickness(9, 4, 9, 4),
            Margin          = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        b.Child = new TextBlock
        {
            Text       = text,
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Br(fgKey)
        };
        return b;
    }

    private Grid MakeInfoRow(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text       = label,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);

        var val = new TextBlock
        {
            Text       = value,
            FontSize   = 12,
            Foreground = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(val, 1);

        g.Children.Add(lbl);
        g.Children.Add(val);
        return g;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ── PAYMENT MODAL ─────────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private void OpenPaymentModal(StorePluginDto p)
    {
        _modalPlugin = p;

        TxtModalPluginName.Text    = p.Name;
        TxtModalPricingBadge.Text  = p.PricingType switch
        {
            "paid"         => p.Price > 0 ? $"Pago único  ·  ${p.Price:0.##}" : "Pago único",
            "donation"     => "Donación  (paga lo que quieras)",
            "subscription" => p.Price > 0 ? $"Suscripción  ·  ${p.Price:0.##}/mes" : "Suscripción mensual",
            _              => "Gratis"
        };

        ModalBodyPanel.Children.Clear();
        switch (p.PricingType)
        {
            case "paid":         BuildPaidModalContent(p);         break;
            case "donation":     BuildDonationModalContent(p);     break;
            case "subscription": BuildSubscriptionModalContent(p); break;
            default:             BuildFreeModalContent(p);         break;
        }

        ModalOverlay.Visibility = Visibility.Visible;
    }

    private void CloseModal()
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        ModalBodyPanel.Children.Clear();
        _modalPlugin = null;
    }

    private void BtnModalClose_Click(object s, RoutedEventArgs e) => CloseModal();

    private void ModalOverlay_MouseDown(object s, MouseButtonEventArgs e)
    {
        // Close only when clicking the dim background, not the card itself
        if (e.Source == ModalOverlay)
            CloseModal();
    }

    private void ModalCard_MouseDown(object s, MouseButtonEventArgs e)
    {
        // Stop the click from bubbling up to ModalOverlay_MouseDown
        e.Handled = true;
    }

    // ── Modal content builders ────────────────────────────────────────────────

    private void BuildFreeModalContent(StorePluginDto p)
    {
        ModalBodyPanel.Children.Add(new TextBlock
        {
            Text         = p.Description,
            FontSize     = 13,
            Foreground   = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 20,
            Margin       = new Thickness(0, 0, 0, 24)
        });

        var btn = new WpfButton
        {
            Content                    = "Instalar",
            Style                      = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment        = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Height                     = 42
        };
        btn.Click += async (_, _) =>
        {
            CloseModal();
            await InstallPluginDirectAsync(p);
        };
        ModalBodyPanel.Children.Add(btn);
    }

    private void BuildDonationModalContent(StorePluginDto p)
    {
        ModalBodyPanel.Children.Add(new TextBlock
        {
            Text         = "Este plugin es gratis. Podés donar lo que quieras para apoyar al desarrollador.",
            FontSize     = 13,
            Foreground   = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 20,
            Margin       = new Thickness(0, 0, 0, 20)
        });

        // Quick-amount chips
        var chipRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        // Plain TextBox (no custom ControlTemplate) so Foreground is guaranteed to render correctly.
        // Wrapped in a Border to provide rounded corners matching the app style.
        var customAmountBox = new WpfTextBox
        {
            Height                     = 36,
            VerticalContentAlignment   = VerticalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Padding                    = new Thickness(10, 0, 10, 0),
            Background                 = new WpfBrush(System.Windows.Media.Color.FromRgb(0x11, 0x11, 0x15)),
            Foreground                 = new WpfBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xF2)),
            SelectionBrush             = Br("AccentBrush"),
            CaretBrush                 = Br("AccentBrush"),
            BorderThickness            = new Thickness(0),
            FontSize                   = 13,
        };
        var amountWrapper = new Border
        {
            Width           = 120,
            Height          = 38,
            BorderBrush     = Br("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(0, 0, 0, 16),
            Child           = customAmountBox
        };

        foreach (var amount in new[] { 1, 2, 5 })
        {
            var chip = new WpfButton { Content = $"${amount}", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
            chip.Click += (_, _) => customAmountBox.Text = amount.ToString();
            chipRow.Children.Add(chip);
        }
        ModalBodyPanel.Children.Add(chipRow);
        ModalBodyPanel.Children.Add(new TextBlock
        {
            Text = "O ingresá un monto personalizado (USD):", FontSize = 12,
            Foreground = Br("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 6)
        });
        ModalBodyPanel.Children.Add(amountWrapper);

        // Donate button — opens PayPal Checkout
        var btnDonate = new WpfButton
        {
            Content                    = "Donar e instalar",
            Style                      = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment        = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Height                     = 42,
            Margin                     = new Thickness(0, 12, 0, 10)
        };
        btnDonate.Click += async (_, _) =>
        {
            if (!decimal.TryParse(customAmountBox.Text.Replace(",", "."),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal donAmt) || donAmt <= 0)
            {
                ShowToast("Ingresá un monto válido (ej: 5)", isError: true);
                return;
            }
            int cents = (int)Math.Round(donAmt * 100);
            await StartCheckoutAsync(p, cents);
        };
        ModalBodyPanel.Children.Add(btnDonate);

        // Skip donation — free install
        var btnSkip = new WpfButton
        {
            Content                    = "Instalar sin donar",
            Style                      = (Style)FindResource("GhostButton"),
            HorizontalAlignment        = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Height                     = 38
        };
        btnSkip.Click += async (_, _) =>
        {
            CloseModal();
            await InstallPluginDirectAsync(p);
        };
        ModalBodyPanel.Children.Add(btnSkip);
    }

    private void BuildPaidModalContent(StorePluginDto p)
    {
        ModalBodyPanel.Children.Add(new TextBlock
        {
            Text = p.Description, FontSize = 13, Foreground = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 20)
        });

        if (p.Price > 0)
        {
            ModalBodyPanel.Children.Add(new TextBlock
            {
                Text       = $"Total: ${p.Price:0.##}",
                FontSize   = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Br("TextPrimaryBrush"),
                Margin     = new Thickness(0, 0, 0, 24)
            });
        }

        // PayPal info note
        var noteBorder = new Border
        {
            Background      = Br("BgCardBrush"),
            BorderBrush     = Br("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(14, 10, 14, 10),
            Margin          = new Thickness(0, 0, 0, 20)
        };
        noteBorder.Child = new TextBlock
        {
            Text         = "El pago se procesa de forma segura a través de PayPal. Al completarlo, volvé aquí e ingresá tu email para verificar y descargar el plugin.",
            FontSize     = 12,
            Foreground   = Br("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18
        };
        ModalBodyPanel.Children.Add(noteBorder);

        var btnPay = new WpfButton
        {
            Content                    = "Pagar con PayPal  →",
            Style                      = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment        = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Height                     = 42
        };
        int cents = p.Price > 0 ? (int)Math.Round(p.Price * 100) : 100;
        btnPay.Click += async (_, _) => await StartCheckoutAsync(p, cents);
        ModalBodyPanel.Children.Add(btnPay);
    }

    private void BuildSubscriptionModalContent(StorePluginDto p)
    {
        ModalBodyPanel.Children.Add(new TextBlock
        {
            Text = p.Description, FontSize = 13, Foreground = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 20)
        });

        var noticeText = p.Price > 0
            ? $"Se cobrarán ${p.Price:0.##} por mes. Podés cancelar cuando quieras."
            : "Suscripción mensual. Podés cancelar cuando quieras.";
        var noticeBorder = new Border
        {
            Background      = Br("BgCardBrush"),
            BorderBrush     = Br("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(14, 10, 14, 10),
            Margin          = new Thickness(0, 0, 0, 20)
        };
        noticeBorder.Child = new TextBlock
        {
            Text         = noticeText + "\n\nEl pago se procesa de forma segura a través de PayPal. Al completarlo, volvé aquí e ingresá tu email para verificar y descargar el plugin.",
            FontSize     = 12,
            Foreground   = Br("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18
        };
        ModalBodyPanel.Children.Add(noticeBorder);

        var btnSub = new WpfButton
        {
            Content                    = "Suscribirse con PayPal  →",
            Style                      = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment        = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Height                     = 42,
            Margin                     = new Thickness(0, 4, 0, 0)
        };
        int cents = p.Price > 0 ? (int)Math.Round(p.Price * 100) : 100;
        btnSub.Click += async (_, _) => await StartCheckoutAsync(p, cents);
        ModalBodyPanel.Children.Add(btnSub);
    }

    // ── Input field helpers ───────────────────────────────────────────────────

    private static TextBlock MakeInputLabel(string text) => new TextBlock
    {
        Text       = text,
        FontSize   = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMutedBrush"),
        Margin     = new Thickness(0, 0, 0, 5)
    };

    private WpfTextBox MakeTextInput(string placeholder, Thickness? Margin = null) => new WpfTextBox
    {
        Style                    = (Style)FindResource("SearchBox"),
        Height                   = 38,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin                   = Margin ?? new Thickness(0),
        Tag                      = placeholder   // used as watermark hint by SearchBox style if supported
    };

    // ══════════════════════════════════════════════════════════════════════════
    // ── INSTALL FLOW ──────────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private async System.Threading.Tasks.Task InstallPluginDirectAsync(StorePluginDto p, string? overrideDownloadUrl = null)
    {
        if (_installingIds.Contains(p.Id)) return;

        _installingIds.Add(p.Id);
        RenderStoreList(_storePluginsData);
        if (_detailPlugin?.Id == p.Id) BuildDetailContent(p);

        try
        {
            // Use override URL (secure token download for paid plugins) or default free endpoint
            const string DlBase = "https://santiagorada.com/focus-tracker/uploads/download.php";
            var url  = overrideDownloadUrl ?? $"{DlBase}?id={Uri.EscapeDataString(p.Id)}";
            var data = await _storeHttp.GetByteArrayAsync(url);

            // Write to a temp .focusplugin file
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{p.Id}.focusplugin");
            await System.IO.File.WriteAllBytesAsync(tmp, data);

            // Install via PluginManager — returns null on success, error string on failure
            var installErr = FocusTracker.Plugins.PluginManager.Instance.Install(tmp);

            // Clean up temp file
            try { System.IO.File.Delete(tmp); } catch { }

            if (installErr != null)
            {
                ShowToast($"Error al instalar: {installErr}", isError: true);
                return;
            }

            // Refresh all plugin-aware UI panels
            LoadPluginSettingCards();
            LoadHomePluginContent();

            ShowToast($"\"{p.Name}\" instalado correctamente.");

            // Redirect to "Mis plugins" so the user sees the newly installed plugin
            ShowMyPlugins();
        }
        catch (Exception ex)
        {
            ShowToast($"Error al instalar: {ex.Message}", isError: true);
        }
        finally
        {
            _installingIds.Remove(p.Id);
            RenderStoreList(_storePluginsData);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ── TOAST ─────────────────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private void ShowToast(string msg, bool isError = false)
    {
        // Stop any running toast timer
        _toastTimer?.Stop();

        ToastText.Text = msg;

        if (isError)
        {
            ToastIcon.Text       = "\uE783"; // Error / warning glyph
            ToastBorder.Background   = new WpfBrush(WpfColor.FromRgb(0x3A, 0x10, 0x10));
            ToastBorder.BorderBrush  = new WpfBrush(WpfColor.FromRgb(0xC0, 0x30, 0x30));
            ToastIcon.Foreground     = new WpfBrush(WpfColor.FromRgb(0xFF, 0x55, 0x55));
        }
        else
        {
            ToastIcon.Text       = "\uE73E"; // Checkmark glyph
            ToastBorder.Background   = new WpfBrush(WpfColor.FromRgb(0x10, 0x20, 0x10));
            ToastBorder.BorderBrush  = Br("AccentBrush");
            ToastIcon.Foreground     = Br("AccentBrush");
        }
        ToastText.Foreground = Br("TextPrimaryBrush");

        ToastBorder.Visibility = Visibility.Visible;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ── APP UPDATE ────────────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private const string ApiBase = "https://santiagorada.com/focus-tracker/api";

    /// <summary>
    /// Called on startup (with internet). Hits /v1/updates/check, and if a newer
    /// version exists either shows the sidebar Update button or auto-installs it.
    /// </summary>
    private async System.Threading.Tasks.Task CheckForUpdateAsync()
    {
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var current = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";

            var json = await _storeHttp.GetStringAsync($"{ApiBase}/v1/updates/check?version={current}");
            using var doc  = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("has_update", out var huProp) || !huProp.GetBoolean())
                return;

            _updateDownloadUrl = root.TryGetProperty("download_url", out var dlProp)
                                 ? dlProp.GetString() : null;
            if (string.IsNullOrEmpty(_updateDownloadUrl)) return;

            var latest = root.TryGetProperty("latest_version", out var lvProp)
                         ? (lvProp.GetString() ?? "?") : "?";

            await Dispatcher.InvokeAsync(() =>
            {
                if (App.Settings.AutoUpdate)
                {
                    _ = PerformUpdateAsync(_updateDownloadUrl);
                }
                else
                {
                    TxtUpdateLabel.Text         = $"Actualizar a v{latest}";
                    BtnSidebarUpdate.ToolTip    = $"Nueva versión disponible: v{latest}";
                    BtnSidebarUpdate.Visibility = Visibility.Visible;
                }
            });
        }
        catch { /* no internet / server unavailable — fail silently */ }
    }

    /// <summary>
    /// Downloads the installer .exe to a temp path, launches it, then closes the app.
    /// If the download fails, falls back to showing the sidebar Update button.
    /// </summary>
    private async System.Threading.Tasks.Task PerformUpdateAsync(string downloadUrl)
    {
        try
        {
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FocusTrackerUpdate.exe");
            var data     = await _storeHttp.GetByteArrayAsync(downloadUrl);
            await System.IO.File.WriteAllBytesAsync(tempPath, data);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = tempPath,
                UseShellExecute = true,
            });

            await Dispatcher.InvokeAsync(() => System.Windows.Application.Current.Shutdown());
        }
        catch
        {
            // Download failed — show the button so the user can retry manually
            await Dispatcher.InvokeAsync(() =>
            {
                if (BtnSidebarUpdate != null)
                    BtnSidebarUpdate.Visibility = Visibility.Visible;
            });
        }
    }

    private async void BtnSidebarUpdate_Click(object s, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_updateDownloadUrl))
            await PerformUpdateAsync(_updateDownloadUrl);
    }

    private void ToggleAutoUpdate_Changed(object s, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        App.Settings.AutoUpdate = ToggleAutoUpdate.IsChecked == true;
        App.Settings.Save();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ── PAYPAL PAYMENT FLOW ───────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Step 1 — Creates a PayPal order/subscription on the server, opens the browser,
    /// then transitions the modal to the email-verification step.
    /// </summary>
    private async System.Threading.Tasks.Task StartCheckoutAsync(StorePluginDto p, int amountCents)
    {
        // Disable the clicked button while we contact the server
        foreach (var child in ModalBodyPanel.Children.OfType<WpfButton>())
            child.IsEnabled = false;

        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                plugin_id    = p.Id,
                plugin_name  = p.Name,
                pricing_type = p.PricingType,
                amount_cents = amountCents,
                currency     = string.IsNullOrWhiteSpace(p.Currency) ? "usd" : p.Currency.ToLower()
            });

            var response = await _storeHttp.PostAsync(
                $"{ApiBase}/create-order.php",
                new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();

            string? checkoutUrl = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("checkout_url", out var urlProp))
                    checkoutUrl = urlProp.GetString();
                else if (root.TryGetProperty("error", out var errProp))
                    throw new Exception(errProp.GetString());
            }
            catch (Exception ex)
            {
                ShowToast($"Error al iniciar el pago: {ex.Message}", isError: true);
                return;
            }

            if (string.IsNullOrEmpty(checkoutUrl))
            {
                ShowToast("No se pudo obtener la URL de pago.", isError: true);
                return;
            }

            // Open PayPal Checkout in the default browser
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = checkoutUrl,
                UseShellExecute = true
            });

            // Transition the modal to email-verification step
            ShowVerificationModalContent(p);
        }
        catch (Exception ex)
        {
            ShowToast($"Error de red: {ex.Message}", isError: true);
            foreach (var child in ModalBodyPanel.Children.OfType<WpfButton>())
                child.IsEnabled = true;
        }
    }

    /// <summary>
    /// Step 2 — Replaces modal body with an email-entry form so the user can
    /// verify their purchase after completing PayPal Checkout in the browser.
    /// </summary>
    private void ShowVerificationModalContent(StorePluginDto p)
    {
        ModalBodyPanel.Children.Clear();

        // "Checkout opened" confirmation
        var confirmRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        confirmRow.Children.Add(new TextBlock
        {
            Text       = "\uE73E",   // checkmark icon
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize   = 16,
            Foreground = Br("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 10, 0)
        });
        confirmRow.Children.Add(new TextBlock
        {
            Text         = "Checkout abierto en tu navegador.\nCompletá el pago y volvé aquí.",
            FontSize     = 13,
            Foreground   = Br("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 20,
            VerticalAlignment = VerticalAlignment.Center
        });
        ModalBodyPanel.Children.Add(confirmRow);

        // Email label
        ModalBodyPanel.Children.Add(new TextBlock
        {
            Text       = "Email que usaste para pagar:",
            FontSize   = 12,
            Foreground = Br("TextMutedBrush"),
            Margin     = new Thickness(0, 0, 0, 6)
        });

        // Email input — pre-filled if the user has purchased before
        var emailBox = new WpfTextBox
        {
            Text                     = App.Settings.LicenseEmail,
            FontSize                 = 13,
            Height                   = 38,
            Padding                  = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = new WpfBrush(WpfColor.FromRgb(0x0F, 0x0F, 0x15)),
            Foreground               = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            BorderBrush              = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness          = new Thickness(1),
            Margin                   = new Thickness(0, 0, 0, 16)
        };
        ModalBodyPanel.Children.Add(emailBox);

        // Error label (hidden until needed)
        var errLabel = new TextBlock
        {
            FontSize   = 12,
            Foreground = new WpfBrush(WpfColor.FromRgb(0xFF, 0x55, 0x55)),
            Margin     = new Thickness(0, -10, 0, 10),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        ModalBodyPanel.Children.Add(errLabel);

        // Verify button
        var btnVerify = new WpfButton
        {
            Content                    = "Verificar y descargar",
            Style                      = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment        = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Height                     = 42,
            Margin                     = new Thickness(0, 0, 0, 10)
        };
        btnVerify.Click += async (_, _) => await VerifyAndInstallAsync(p, emailBox, btnVerify, errLabel);
        ModalBodyPanel.Children.Add(btnVerify);

        // Allow pressing Enter in the email box to verify
        emailBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Return) await VerifyAndInstallAsync(p, emailBox, btnVerify, errLabel);
        };

        // Cancel / try later link
        var btnCancel = new WpfButton
        {
            Content             = "Cancelar",
            Style               = (Style)FindResource("GhostButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Height              = 36
        };
        btnCancel.Click += (_, _) => CloseModal();
        ModalBodyPanel.Children.Add(btnCancel);

        // Focus the email field
        emailBox.Focus();
        emailBox.SelectAll();
    }

    /// <summary>
    /// Step 3 — Calls verify-license.php with the email. On success, saves the email,
    /// builds the secure download URL from the returned token, and installs the plugin.
    /// </summary>
    private async System.Threading.Tasks.Task VerifyAndInstallAsync(
        StorePluginDto p,
        WpfTextBox emailBox,
        WpfButton btnVerify,
        TextBlock errLabel)
    {
        var email = emailBox.Text.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            errLabel.Text       = "Ingresá un email válido.";
            errLabel.Visibility = Visibility.Visible;
            return;
        }

        btnVerify.IsEnabled = false;
        btnVerify.Content   = "Verificando…";
        errLabel.Visibility = Visibility.Collapsed;

        try
        {
            var url = $"{ApiBase}/verify-license.php" +
                      $"?plugin_id={Uri.EscapeDataString(p.Id)}" +
                      $"&email={Uri.EscapeDataString(email)}";

            var response = await _storeHttp.GetAsync(url);
            var body     = await response.Content.ReadAsStringAsync();

            bool licensed = false;
            string? token = null;

            using var doc  = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("licensed", out var lp)) licensed = lp.GetBoolean();
            if (licensed && root.TryGetProperty("download_token", out var tp)) token = tp.GetString();

            if (!licensed || string.IsNullOrEmpty(token))
            {
                errLabel.Text       = "No encontramos un pago con ese email. Si acabás de pagar, esperá unos segundos y reintentá.";
                errLabel.Visibility = Visibility.Visible;
                btnVerify.IsEnabled = true;
                btnVerify.Content   = "Verificar y descargar";
                return;
            }

            // Save email for next time
            App.Settings.LicenseEmail = email;
            App.Settings.Save();

            // Build secure download URL and install
            var secureUrl = $"{ApiBase}/secure-download.php" +
                            $"?id={Uri.EscapeDataString(p.Id)}" +
                            $"&token={Uri.EscapeDataString(token)}";

            CloseModal();
            await InstallPluginDirectAsync(p, secureUrl);
        }
        catch (Exception ex)
        {
            errLabel.Text       = $"Error de red: {ex.Message}";
            errLabel.Visibility = Visibility.Visible;
            btnVerify.IsEnabled = true;
            btnVerify.Content   = "Verificar y descargar";
        }
    }
}