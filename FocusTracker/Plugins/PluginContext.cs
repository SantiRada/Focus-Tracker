using System.Media;
using FocusTracker.Data;
using FocusTracker.Models;
using FocusTracker.Services;
using FocusTracker.Settings;

namespace FocusTracker.Plugins;

/// <summary>
/// The sandboxed API surface given to every plugin during <see cref="IFocusPlugin.Initialize"/>.
/// All methods are thread-safe; callbacks from events are marshalled to the UI thread
/// by the host before reaching plugin code.
/// </summary>
public sealed class PluginContext : IPluginContext
{
    private readonly DatabaseService  _db;
    private readonly TrackingService  _tracker;
    private readonly PluginRegistry   _registry;
    private readonly string           _pluginId;

    internal PluginContext(
        DatabaseService db,
        TrackingService tracker,
        PluginRegistry  registry,
        string          pluginId)
    {
        _db       = db;
        _tracker  = tracker;
        _registry = registry;
        _pluginId = pluginId;
    }

    // ═════════════════════════════════════════════════════════════════════
    // GETTERS — Tracking state
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <c>true</c> while a tracking session is active.
    /// </summary>
    public bool IsTracking => _tracker.IsTracking;

    /// <summary>
    /// Returns the display name of the application or URL currently in focus,
    /// or <c>null</c> when nothing tracked is focused.
    /// </summary>
    public string? GetCurrentFocusedApp() => _tracker.CurrentFocusedApp;

    /// <summary>
    /// Fires on the UI thread whenever the focused application changes.
    /// <para>Parameters: <c>(string displayName, TimeSpan sessionDuration)</c></para>
    /// </summary>
    public event Action<string, TimeSpan>? FocusChanged
    {
        add    => _tracker.FocusChanged += value;
        remove => _tracker.FocusChanged -= value;
    }

    /// <summary>
    /// Fires on the UI thread when a session or project time alarm is triggered.
    /// <para>Parameters: <c>(string alarmTitle, string alarmMessage)</c></para>
    /// </summary>
    public event Action<string, string>? AlarmTriggered
    {
        add    => _tracker.AlarmTriggered += value;
        remove => _tracker.AlarmTriggered -= value;
    }

    /// <summary>
    /// Fires on the UI thread when the idle-detection threshold is exceeded
    /// and tracking is automatically stopped.
    /// </summary>
    public event Action? IdleDetected
    {
        add    => _tracker.IdleDetected += value;
        remove => _tracker.IdleDetected -= value;
    }

    /// <summary>Fires on the UI thread right after a tracking session starts.</summary>
    public event Action<int?>? TrackingStarted
    {
        add    => _registry.AddTrackingStartedHandler(value!);
        remove => _registry.RemoveTrackingStartedHandler(value!);
    }

    /// <summary>Fires on the UI thread right after a tracking session stops.</summary>
    public event Action? TrackingStopped
    {
        add    => _registry.AddTrackingStoppedHandler(value!);
        remove => _registry.RemoveTrackingStoppedHandler(value!);
    }

    // ═════════════════════════════════════════════════════════════════════
    // GETTERS — Sessions
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns all recorded tracking sessions ordered by start time descending.
    /// Includes both open (no EndTime) and closed sessions.
    /// </summary>
    public List<TrackingSession> GetAllSessions()
        => _db.GetAllSessions();

    /// <summary>
    /// Returns only the sessions whose start time falls within [<paramref name="from"/>, <paramref name="to"/>].
    /// </summary>
    public List<TrackingSession> GetSessionsInRange(DateTime from, DateTime to)
        => _db.GetAllSessions()
              .Where(s => s.StartTime >= from && s.StartTime <= to)
              .ToList();

    /// <summary>
    /// Returns the currently active (open) session, or <c>null</c> if no tracking is running.
    /// </summary>
    public TrackingSession? GetCurrentSession()
    {
        if (!_tracker.IsTracking) return null;
        return _db.GetAllSessions()
                  .FirstOrDefault(s => s.Id == _tracker.CurrentSessionId);
    }

    /// <summary>
    /// Returns per-app usage summaries for a specific session.
    /// Each entry aggregates focus time and unfocus time for one process/URL.
    /// </summary>
    /// <param name="sessionId">The <see cref="TrackingSession.Id"/> to query.</param>
    public List<AppUsageSummary> GetSessionDetail(int sessionId)
        => _db.GetSessionDetail(sessionId);

    // ═════════════════════════════════════════════════════════════════════
    // GETTERS — Usage summaries
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns per-app usage summaries for all sessions whose events fall within
    /// [<paramref name="from"/>, <paramref name="to"/>], optionally filtered to a project.
    /// Results are ordered by total time descending.
    /// </summary>
    /// <param name="from">Inclusive start of the time window.</param>
    /// <param name="to">Inclusive end of the time window.</param>
    /// <param name="projectId">
    /// When provided, only events belonging to sessions linked to this project are included.
    /// </param>
    public List<AppUsageSummary> GetUsageSummaries(DateTime from, DateTime to, int? projectId = null)
        => _db.GetUsageSummaries(from, to, projectId);

    /// <summary>
    /// Convenience overload — returns usage summaries for today (midnight → now).
    /// </summary>
    public List<AppUsageSummary> GetTodaySummaries(int? projectId = null)
    {
        var today = DateTime.Today;
        return GetUsageSummaries(today, today.AddDays(1).AddTicks(-1), projectId);
    }

    /// <summary>
    /// Returns the total tracked time (focus + unfocus) across all events in a date range.
    /// </summary>
    public TimeSpan GetTotalTrackedTime(DateTime from, DateTime to, int? projectId = null)
    {
        var summaries = GetUsageSummaries(from, to, projectId);
        return TimeSpan.FromSeconds(summaries.Sum(s => s.FocusSeconds + s.UnfocusSeconds));
    }

    // ═════════════════════════════════════════════════════════════════════
    // GETTERS — Projects
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Returns all projects ordered alphabetically by name.</summary>
    public List<Project> GetAllProjects()
        => _db.GetAllProjects();

    /// <summary>
    /// Returns the project with the given <paramref name="id"/>,
    /// or <c>null</c> if it does not exist.
    /// </summary>
    public Project? GetProject(int id)
        => _db.GetAllProjects().FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Returns usage summaries for a project split into two lists:
    /// <list type="bullet">
    ///   <item><description><c>main</c> — apps/URLs explicitly tracked by the project.</description></item>
    ///   <item><description><c>extras</c> — apps/URLs that appeared in project sessions but are not in its tracked keys.</description></item>
    /// </list>
    /// </summary>
    public (List<AppUsageSummary> main, List<AppUsageSummary> extras)
        GetProjectSummaries(Project project, DateTime from, DateTime to)
        => _db.GetProjectSummaries(project, from, to);

    // ═════════════════════════════════════════════════════════════════════
    // GETTERS — Settings (read-only)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the path to the folder where FocusTracker stores its database.
    /// Use this if your plugin needs to read or write files alongside the main DB.
    /// </summary>
    public string GetDataFolder() => AppSettings.Instance.DataFolder;

    /// <summary>Returns whether the host has notification sounds enabled.</summary>
    public bool GetNotificationSoundEnabled() => AppSettings.Instance.NotificationSound;

    /// <summary>Returns whether idle-detection is enabled in the host settings.</summary>
    public bool GetIdleDetectionEnabled() => AppSettings.Instance.IdleDetection;

    // ═════════════════════════════════════════════════════════════════════
    // SETTERS — Projects
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new project and returns its auto-assigned ID.
    /// </summary>
    /// <param name="name">Display name of the project. Must not be empty.</param>
    /// <param name="trackedKeys">
    /// Process names (e.g. <c>"code"</c>, <c>"figma"</c>) or URL hosts (e.g. <c>"github.com"</c>)
    /// to associate with this project.
    /// </param>
    /// <param name="totalAlarmMins">
    /// Optional: total accumulated time alarm in minutes.
    /// The host will fire <see cref="AlarmTriggered"/> when exceeded.
    /// </param>
    /// <param name="sessionAlarmMins">
    /// Optional: per-session time alarm in minutes.
    /// </param>
    /// <returns>The new project's integer ID.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty.</exception>
    public int CreateProject(
        string   name,
        string[] trackedKeys,
        int?     totalAlarmMins   = null,
        int?     sessionAlarmMins = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name must not be empty.", nameof(name));

        return _db.CreateProject(
            name.Trim(),
            string.Join(",", trackedKeys.Select(k => k.Trim()).Where(k => k.Length > 0)),
            totalAlarmMins   == null ? null : totalAlarmMins   * 60,
            sessionAlarmMins == null ? null : sessionAlarmMins * 60);
    }

    /// <summary>
    /// Updates an existing project's name, tracked keys, and alarm thresholds.
    /// </summary>
    /// <param name="id">ID of the project to update.</param>
    /// <param name="name">New display name.</param>
    /// <param name="trackedKeys">New set of process/URL keys.</param>
    /// <param name="totalAlarmMins">New total-time alarm in minutes (<c>null</c> to remove).</param>
    /// <param name="sessionAlarmMins">New session-time alarm in minutes (<c>null</c> to remove).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty.</exception>
    public void UpdateProject(
        int      id,
        string   name,
        string[] trackedKeys,
        int?     totalAlarmMins   = null,
        int?     sessionAlarmMins = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name must not be empty.", nameof(name));

        _db.UpdateProject(
            id,
            name.Trim(),
            string.Join(",", trackedKeys.Select(k => k.Trim()).Where(k => k.Length > 0)),
            totalAlarmMins   == null ? null : totalAlarmMins   * 60,
            sessionAlarmMins == null ? null : sessionAlarmMins * 60);
    }

    /// <summary>
    /// Permanently deletes a project. Sessions previously linked to it are preserved
    /// but their <c>ProjectId</c> reference is set to <c>null</c>.
    /// </summary>
    public void DeleteProject(int id)
        => _db.DeleteProject(id);

    // ═════════════════════════════════════════════════════════════════════
    // SETTERS — Sessions
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Renames an existing session.
    /// </summary>
    /// <param name="sessionId">The session to rename.</param>
    /// <param name="name">New display name. Pass an empty string to restore the default label.</param>
    public void RenameSession(int sessionId, string name)
        => _db.UpdateSessionName(sessionId, name);

    /// <summary>
    /// Links or unlinks a session from a project.
    /// </summary>
    /// <param name="sessionId">The session to update.</param>
    /// <param name="projectId">Target project ID, or <c>null</c> to unlink.</param>
    public void AssignSessionToProject(int sessionId, int? projectId)
        => _db.UpdateSessionProject(sessionId, projectId);

    /// <summary>
    /// Permanently deletes a session and all its focus events.
    /// </summary>
    /// <remarks>
    /// This is a destructive, irreversible operation. Always confirm with the user before calling.
    /// </remarks>
    public void DeleteSession(int sessionId)
        => _db.DeleteSession(sessionId);

    // ═════════════════════════════════════════════════════════════════════
    // SETTERS — Active tracking
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds an application to the currently active tracking session at runtime.
    /// Has no effect if tracking is not running.
    /// </summary>
    /// <param name="processName">Process name without .exe extension (e.g. <c>"slack"</c>).</param>
    /// <param name="displayName">Human-readable label shown in the UI and reports.</param>
    /// <param name="trackUnfocus">
    /// When <c>true</c>, time spent with the app open but not focused is also recorded.
    /// </param>
    public void AddTrackedApp(string processName, string displayName, bool trackUnfocus = false)
    {
        _tracker.AddProcess(new FocusTracker.Models.InstalledApp
        {
            ExecutableName = processName.Trim().ToLower(),
            DisplayName    = displayName,
            IsSelected     = true,
            TrackUnfocus   = trackUnfocus
        });
    }

    /// <summary>
    /// Adds a URL host to the currently active tracking session at runtime.
    /// Has no effect if tracking is not running.
    /// </summary>
    /// <param name="host">Domain to track (e.g. <c>"github.com"</c>). Sub-domains match automatically.</param>
    /// <param name="label">Human-readable label shown in the UI and reports.</param>
    /// <param name="trackUnfocus">
    /// When <c>true</c>, time spent with the page open in a background tab is also recorded.
    /// </param>
    public void AddTrackedUrl(string host, string label, bool trackUnfocus = false)
    {
        _tracker.AddUrl(new FocusTracker.Models.TrackedUrl
        {
            Host         = host.Trim().ToLower(),
            Label        = label,
            RawInput     = host,
            IsSelected   = true,
            TrackUnfocus = trackUnfocus
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // SETTINGS — Plugin contributions
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a setting item that will be rendered inside the host's Settings page.
    /// The item is placed under the tab named <see cref="PluginSettingDescriptor.TabName"/>;
    /// the tab is created automatically if it does not exist.
    /// </summary>
    public void RegisterSetting(PluginSettingDescriptor descriptor)
        => _registry.RegisterSetting(_pluginId, descriptor);

    /// <summary>
    /// Clears all previously registered settings for this plugin, invokes
    /// <paramref name="reRegisterCallback"/> (which should call <see cref="RegisterSetting"/>
    /// for every desired setting), then rebuilds the settings UI immediately.
    /// Safe to call from any thread; the UI rebuild is dispatched to the UI thread.
    /// </summary>
    public void RefreshSettings(Action reRegisterCallback)
    {
        // 1. Remove existing descriptors for this plugin
        _registry.ClearSettingsForPlugin(_pluginId);

        // 2. Let the plugin re-register its current descriptor set
        try { reRegisterCallback(); }
        catch { /* plugin fault — settings may be partially registered */ }

        // 3. Tell MainWindow to rebuild the settings panel on the UI thread
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => PluginRegistry.RequestSettingsRefresh());
    }

    /// <summary>
    /// Navigates the host to the Settings page and activates this plugin's tab.
    /// Safe to call from any thread.
    /// </summary>
    public void NavigateToPluginSettings()
    {
        // Resolve the tab name from any registered setting for this plugin
        var tabName = _registry.GetAllSettings()
            .Where(s => s.PluginId == _pluginId)
            .Select(s => s.Descriptor.TabName)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(tabName)) return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => PluginRegistry.RequestNavigateToPluginSettings(tabName));
    }

    // ═════════════════════════════════════════════════════════════════════
    // SCREEN CONTRIBUTIONS
    // ═════════════════════════════════════════════════════════════════════

    public void RegisterButton(PluginButtonContribution button)
        => _registry.RegisterButton(_pluginId, button);

    public void RegisterHomeCard(PluginCardContribution card)
        => _registry.RegisterHomeCard(_pluginId, card);

    // ═════════════════════════════════════════════════════════════════════
    // NOTIFICATIONS & SOUND
    // ═════════════════════════════════════════════════════════════════════

    public void ShowNotification(string title, string message,
                                 PluginToastKind kind = PluginToastKind.Success)
        => Views.ToastWindow.ShowTemporary(title, message, MapKind(kind));

    public void ShowPersistentNotification(string title, string message,
                                           PluginToastKind kind = PluginToastKind.Success,
                                           IReadOnlyList<PluginToastAction>? actions = null)
        => Views.ToastWindow.ShowPersistent(title, message, MapKind(kind), actions);

    /// <summary>
    /// Plays a Windows system sound only if the user has notification sounds enabled.
    /// </summary>
    public void PlaySound(WindowsSystemSound sound = WindowsSystemSound.Asterisk)
    {
        if (!AppSettings.Instance.NotificationSound) return;
        PlaySystemSound(sound);
    }

    public void ShowNotificationWithSound(string title, string message,
                                          PluginToastKind kind     = PluginToastKind.Success,
                                          WindowsSystemSound sound = WindowsSystemSound.Asterisk)
    {
        PlaySystemSound(sound);   // unconditional — bypasses user setting
        Views.ToastWindow.ShowTemporary(title, message, MapKind(kind));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void PlaySystemSound(WindowsSystemSound sound)
    {
        try
        {
            switch (sound)
            {
                case WindowsSystemSound.Beep:        SystemSounds.Beep.Play();        break;
                case WindowsSystemSound.Exclamation: SystemSounds.Exclamation.Play(); break;
                case WindowsSystemSound.Hand:        SystemSounds.Hand.Play();        break;
                case WindowsSystemSound.Question:    SystemSounds.Question.Play();    break;
                default:                             SystemSounds.Asterisk.Play();    break;
            }
        }
        catch { /* audio subsystem unavailable */ }
    }

    private static Views.ToastKind MapKind(PluginToastKind k) => k switch
    {
        PluginToastKind.Info    => Views.ToastKind.Info,
        PluginToastKind.Warning => Views.ToastKind.Warning,
        PluginToastKind.Error   => Views.ToastKind.Error,
        _                       => Views.ToastKind.Success,
    };

    // ═════════════════════════════════════════════════════════════════════
    // UTILITIES
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> into a compact human-readable string consistent
    /// with FocusTracker's UI style.
    /// <list type="bullet">
    ///   <item><description>≥ 1 hour  → "2h 5m 30s"</description></item>
    ///   <item><description>≥ 1 min   → "5m 30s"</description></item>
    ///   <item><description>otherwise → "30s"</description></item>
    /// </list>
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours   >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
        if (duration.TotalMinutes >= 1) return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    /// <summary>
    /// Returns a <see cref="DateTime"/> range for the requested preset relative to today.
    /// Useful for passing to <see cref="GetUsageSummaries"/>.
    /// </summary>
    public static (DateTime from, DateTime to) GetDateRange(DateRangePreset preset)
    {
        var today = DateTime.Today;
        return preset switch
        {
            DateRangePreset.Today     => (today, today.AddDays(1).AddTicks(-1)),
            DateRangePreset.ThisWeek  => (today.AddDays(-(int)today.DayOfWeek + 1),  today.AddDays(1).AddTicks(-1)),
            DateRangePreset.ThisMonth => (new DateTime(today.Year, today.Month, 1),   today.AddDays(1).AddTicks(-1)),
            DateRangePreset.ThisYear  => (new DateTime(today.Year, 1, 1),             today.AddDays(1).AddTicks(-1)),
            _                         => (today, today.AddDays(1).AddTicks(-1))
        };
    }
}
