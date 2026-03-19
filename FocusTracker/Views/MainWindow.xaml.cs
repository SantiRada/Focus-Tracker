using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FocusTracker.Helpers;
using FocusTracker.Models;
using FocusTracker.Services;
using FocusTracker.ViewModels;
using WpfColor       = System.Windows.Media.Color;
using WpfBrush       = System.Windows.Media.SolidColorBrush;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfButton      = System.Windows.Controls.Button;
using WpfCheckBox    = System.Windows.Controls.CheckBox;
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

    public MainWindow()
    {
        InitializeComponent();
        RegisterStartup();

        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        TxtAppVersion.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.4";

        _timeTabs     = new[] { TabToday, TabWeek, TabMonth, TabYear, TabCustom };
        _projTimeTabs = new[] { ProjTabToday, ProjTabWeek, ProjTabMonth, ProjTabYear, ProjTabCustom };

        App.Tracker.FocusChanged += (app, _) => Dispatcher.Invoke(() => TxtCurrentApp.Text = app);
        App.Tracker.AlarmTriggered += (title, msg) => Dispatcher.Invoke(() => ShowAlarmNotification(title, msg));

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += UiTimer_Tick;

        _uiTimer.Start();

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

    private void StartProjectSession(Project proj)
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
        // Tracked URLs list might be empty, that's fine, but let's be safe
        var urls = (_trackedUrls ?? new()).Where(u => keys.Contains($"[{u.Host}]", StringComparer.OrdinalIgnoreCase)).ToList();
        
        if (unify) apps = UnifyVersions(apps);
        foreach (var a in apps) a.TrackUnfocus = unfocus;
        foreach (var u in urls) u.TrackUnfocus = unfocus;

        try 
        {
            App.Tracker.StartTracking(apps, urls, proj.Name, proj.Id, 
                                      sessionAlarmMins: proj.SessionTimeAlarmSeconds.HasValue ? proj.SessionTimeAlarmSeconds.Value / 60 : null,
                                      totalAlarmMins: proj.TotalTimeAlarmSeconds.HasValue ? proj.TotalTimeAlarmSeconds.Value / 60 : null);
            
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
    }

    private void GoToProjects_Click(object s, RoutedEventArgs e) => ShowPage("Projects");

    private void NavDashboard_Click(object s, RoutedEventArgs e) { ShowPage("Dashboard"); LoadDashboard(); }
    private void NavSessions_Click(object s, RoutedEventArgs e)  { ShowPage("Sessions");  LoadSessions();  }
    private void NavHelp_Click(object s, RoutedEventArgs e)     => ShowPage("Help");
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

    private void NavProjects_Click(object s, RoutedEventArgs e)  { ShowPage("Projects");  LoadProjects();  }
    private void BackToSessions_Click(object s, RoutedEventArgs e) { ShowPage("Sessions"); LoadSessions(); }

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

        BtnNavSetup.Tag     = page == "Setup"                                        ? "active" : "";
        BtnNavDashboard.Tag = page == "Dashboard"                                    ? "active" : "";
        BtnNavProjects.Tag  = page is "Projects" or "ProjectDetail" or "ProjectEdit" ? "active" : "";
        BtnNavHelp.Tag      = page == "Help"                                         ? "active" : "";
    }

    private void BtnStop_Click(object s, RoutedEventArgs e)
    {
        var sessionId = App.Tracker.CurrentSessionId;
        App.Tracker.StopTracking();
        
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
        ProjectList.ItemsSource  = vms;
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
        else
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
        TxtNavHelp.Visibility       = vis;
        TxtNavTut.Visibility        = vis;
        TxtStopLabel.Visibility     = vis;

        // Adjust layout for collapsed mode
        ColStatusDot.Width = _sidebarCollapsed ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SidebarStatusArea.Padding = _sidebarCollapsed ? new Thickness(0, 12, 0, 12) : new Thickness(18, 12, 18, 12);

        // Trigger immediate update of sidebar background
        UiTimer_Tick(null, EventArgs.Empty);

        // Adjust button padding for icons only, keeping vertical height consistent
        var padding = _sidebarCollapsed ? new Thickness(0, 10, 0, 10) : new Thickness(10, 10, 10, 10);
        
        foreach (var btn in new[] { BtnNavSetup, BtnNavDashboard, BtnNavProjects, BtnNavHelp, BtnOnboarding, BtnSidebarStop })
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
        if (App.Tracker.IsTracking) App.Tracker.StopTracking();
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
}