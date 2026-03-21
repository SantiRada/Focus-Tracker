// ── FocusTracker.PluginSDK — IPluginContext.cs ───────────────────────────────
// The API surface plugins code against. The host provides the concrete
// implementation; plugins never depend on host internals.
// ─────────────────────────────────────────────────────────────────────────────
using FocusTracker.Models;

namespace FocusTracker.Plugins;

/// <summary>
/// Sandboxed API surface provided to every plugin via
/// <see cref="IFocusPlugin.Initialize"/>.
/// All methods are thread-safe; event callbacks are delivered on the UI thread.
/// </summary>
public interface IPluginContext
{
    // ═══════════════════════════════════════════════════════════════════════
    // TRACKING STATE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True while a tracking session is active.</summary>
    bool IsTracking { get; }

    /// <summary>Display name of the app/URL currently in focus, or null.</summary>
    string? GetCurrentFocusedApp();

    /// <summary>
    /// Fires on the UI thread when the focused app changes.
    /// (displayName, durationOnPrevious)
    /// </summary>
    event Action<string, TimeSpan>? FocusChanged;

    /// <summary>
    /// Fires on the UI thread when a session or project alarm threshold is exceeded.
    /// (title, message)
    /// </summary>
    event Action<string, string>? AlarmTriggered;

    /// <summary>Fires on the UI thread when idle-detection stops tracking.</summary>
    event Action? IdleDetected;

    /// <summary>
    /// Fires on the UI thread right after a tracking session starts.
    /// The parameter is the project ID (or null for free/quick sessions).
    /// </summary>
    event Action<int?>? TrackingStarted;

    /// <summary>Fires on the UI thread right after a tracking session stops.</summary>
    event Action? TrackingStopped;

    // ═══════════════════════════════════════════════════════════════════════
    // SESSIONS — READ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>All sessions ordered by start time descending.</summary>
    List<TrackingSession> GetAllSessions();

    /// <summary>Sessions whose start time falls within [from, to].</summary>
    List<TrackingSession> GetSessionsInRange(DateTime from, DateTime to);

    /// <summary>The currently active session, or null if not tracking.</summary>
    TrackingSession? GetCurrentSession();

    /// <summary>Per-app usage summaries for a single session.</summary>
    List<AppUsageSummary> GetSessionDetail(int sessionId);

    // ═══════════════════════════════════════════════════════════════════════
    // USAGE SUMMARIES — READ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-app summaries for the given range, optionally filtered to a project.
    /// Ordered by total time descending.
    /// </summary>
    List<AppUsageSummary> GetUsageSummaries(DateTime from, DateTime to, int? projectId = null);

    /// <summary>Today's summaries (midnight → now), optionally scoped to a project.</summary>
    List<AppUsageSummary> GetTodaySummaries(int? projectId = null);

    /// <summary>Total tracked time (focus + unfocus) across the given range.</summary>
    TimeSpan GetTotalTrackedTime(DateTime from, DateTime to, int? projectId = null);

    // ═══════════════════════════════════════════════════════════════════════
    // PROJECTS — READ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>All projects ordered alphabetically.</summary>
    List<Project> GetAllProjects();

    /// <summary>Project by ID, or null if not found.</summary>
    Project? GetProject(int id);

    /// <summary>
    /// Project summaries split into explicitly tracked (main) and incidental (extras).
    /// </summary>
    (List<AppUsageSummary> main, List<AppUsageSummary> extras)
        GetProjectSummaries(Project project, DateTime from, DateTime to);

    // ═══════════════════════════════════════════════════════════════════════
    // SETTINGS — READ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Path to the folder where FocusTracker stores its database.</summary>
    string GetDataFolder();

    /// <summary>Whether the host has notification sounds enabled.</summary>
    bool GetNotificationSoundEnabled();

    /// <summary>Whether idle-detection is enabled in host settings.</summary>
    bool GetIdleDetectionEnabled();

    // ═══════════════════════════════════════════════════════════════════════
    // PROJECTS — WRITE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Creates a project and returns its new ID.</summary>
    /// <exception cref="ArgumentException">When name is empty.</exception>
    int CreateProject(string name, string[] trackedKeys,
                      int? totalAlarmMins = null, int? sessionAlarmMins = null);

    /// <summary>Updates an existing project.</summary>
    void UpdateProject(int id, string name, string[] trackedKeys,
                       int? totalAlarmMins = null, int? sessionAlarmMins = null);

    /// <summary>Deletes a project. Sessions are preserved but unlinked. Irreversible.</summary>
    void DeleteProject(int id);

    // ═══════════════════════════════════════════════════════════════════════
    // SESSIONS — WRITE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Renames a session. Pass empty string to restore default label.</summary>
    void RenameSession(int sessionId, string name);

    /// <summary>Links or unlinks a session from a project.</summary>
    void AssignSessionToProject(int sessionId, int? projectId);

    /// <summary>Permanently deletes a session and all its events. Irreversible.</summary>
    void DeleteSession(int sessionId);

    // ═══════════════════════════════════════════════════════════════════════
    // ACTIVE TRACKING — WRITE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds an app to the running session.
    /// No-op if tracking is stopped.
    /// </summary>
    /// <param name="processName">Process name without .exe, lowercase (e.g. "slack").</param>
    void AddTrackedApp(string processName, string displayName, bool trackUnfocus = false);

    /// <summary>
    /// Adds a URL host to the running session.
    /// Sub-domains match automatically. No-op if tracking is stopped.
    /// </summary>
    void AddTrackedUrl(string host, string label, bool trackUnfocus = false);

    // ═══════════════════════════════════════════════════════════════════════
    // SETTINGS — REGISTER
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contributes a setting item to the host's Settings page under a named tab.
    /// Call during <see cref="IFocusPlugin.Initialize"/>.
    /// </summary>
    void RegisterSetting(PluginSettingDescriptor descriptor);

    /// <summary>
    /// Clears all previously registered settings for this plugin, invokes
    /// <paramref name="reRegisterCallback"/> (which should call <see cref="RegisterSetting"/>
    /// for every desired setting), then tells the host to rebuild the settings UI.
    /// Use this to update the settings panel dynamically without restarting the app —
    /// for example, to show a new item after it is added, or to hide a conditional field.
    /// </summary>
    void RefreshSettings(Action reRegisterCallback);

    /// <summary>
    /// Navigates the host application to the Settings page and activates the tab
    /// that belongs to this plugin.
    /// </summary>
    void NavigateToPluginSettings();

    // ═══════════════════════════════════════════════════════════════════════
    // SCREEN CONTRIBUTIONS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contributes a button to a host screen.
    /// Buttons appear in a dedicated plugin row below the screen's native controls.
    /// Call during <see cref="IFocusPlugin.Initialize"/>.
    /// </summary>
    void RegisterButton(PluginButtonContribution button);

    /// <summary>
    /// Contributes a data card to the Home screen.
    /// Cards appear below the built-in home content inside their own card UI.
    /// When <see cref="PluginCardContribution.AutoRefreshSeconds"/> is greater than zero
    /// the host will call <see cref="PluginCardContribution.GetRows"/> periodically.
    /// Call during <see cref="IFocusPlugin.Initialize"/>.
    /// </summary>
    void RegisterHomeCard(PluginCardContribution card);

    // ═══════════════════════════════════════════════════════════════════════
    // NOTIFICATIONS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shows a temporary toast that auto-dismisses after a few seconds.
    /// Plays the system sound if the user has notification sounds enabled.
    /// </summary>
    void ShowNotification(string title, string message,
                          PluginToastKind kind = PluginToastKind.Success);

    /// <summary>
    /// Shows a persistent toast that stays on screen until the user dismisses it.
    /// Always shows a close (×) button. Optionally shows up to three action buttons.
    /// Plays the system sound if the user has notification sounds enabled.
    /// </summary>
    void ShowPersistentNotification(string title, string message,
                                    PluginToastKind kind = PluginToastKind.Success,
                                    IReadOnlyList<PluginToastAction>? actions = null);

    /// <summary>
    /// Plays a Windows system sound — but ONLY if the user has notification
    /// sounds enabled in the host's settings. Use this for event-driven audio.
    /// </summary>
    void PlaySound(WindowsSystemSound sound = WindowsSystemSound.Asterisk);

    /// <summary>
    /// Shows a temporary toast AND plays a system sound unconditionally,
    /// bypassing the host's notification-sound setting.
    /// Use for alerts that must always produce audio feedback.
    /// </summary>
    void ShowNotificationWithSound(string title, string message,
                                   PluginToastKind kind      = PluginToastKind.Success,
                                   WindowsSystemSound sound  = WindowsSystemSound.Asterisk);
}
