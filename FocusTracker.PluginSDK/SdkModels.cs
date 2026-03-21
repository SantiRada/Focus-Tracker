// ── FocusTracker.PluginSDK — SdkModels.cs ────────────────────────────────────
// Data-transfer objects that cross the host ↔ plugin boundary.
// These types live in the SDK so both sides share the same assembly reference,
// which guarantees type identity when the host casts a loaded plugin.
// ─────────────────────────────────────────────────────────────────────────────

namespace FocusTracker.Models;

/// <summary>A recorded focus-tracking session.</summary>
public class TrackingSession
{
    public int       Id               { get; set; }
    public string    Name             { get; set; } = "";
    public DateTime  StartTime        { get; set; }
    public DateTime? EndTime          { get; set; }
    public string    TrackedProcesses { get; set; } = "";
    public int?      ProjectId        { get; set; }

    /// <summary>Friendly label: uses <see cref="Name"/> when set, otherwise "Session #Id".</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Session #{Id}" : Name;
}

/// <summary>A user-defined project grouping tracked apps and URLs.</summary>
public class Project
{
    public int     Id          { get; set; }
    public string  Name        { get; set; } = "";
    /// <summary>Comma-separated process names or URL hosts tracked by this project.</summary>
    public string  TrackedKeys { get; set; } = "";
    public int?    TotalTimeAlarmSeconds   { get; set; }
    public int?    SessionTimeAlarmSeconds { get; set; }
    /// <summary>When true, the Pomodoro plugin (if installed) auto-starts with sessions of this project.</summary>
    public bool    UsePomodoro { get; set; }

    // runtime-only — populated by GetProjectSummaries
    public List<TrackingItem> Items { get; set; } = new();
}

/// <summary>Lightweight resolved item from a project's tracked-key string.</summary>
public class TrackingItem
{
    public string Key          { get; set; } = "";
    public string Label        { get; set; } = "";
    public bool   IsUrl        { get; set; }
    public bool   TrackUnfocus { get; set; }
}

/// <summary>
/// Per-app aggregated usage summary for a given time range.
/// Focus time = actively in the foreground; unfocus time = open but not focused.
/// </summary>
public class AppUsageSummary
{
    public string ProcessName    { get; set; } = "";
    public string AppDisplayName { get; set; } = "";
    public long   FocusSeconds   { get; set; }
    public long   UnfocusSeconds { get; set; }
    public int    EventCount     { get; set; }
    public bool   IsUrl          { get; set; }
    /// <summary>True when this entry came from a project session but is not in the project's tracked keys.</summary>
    public bool   IsProjectExtra { get; set; }

    public TimeSpan FocusTime   => TimeSpan.FromSeconds(FocusSeconds);
    public TimeSpan UnfocusTime => TimeSpan.FromSeconds(UnfocusSeconds);
    public TimeSpan TotalTime   => TimeSpan.FromSeconds(FocusSeconds + UnfocusSeconds);

    /// <summary>Pre-formatted total time (e.g. "1h 5m 30s").</summary>
    public string FormattedTime
    {
        get
        {
            var ts = TotalTime;
            if (ts.TotalHours   >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }
}

/// <summary>Preset date ranges for use with <c>IPluginContext.GetUsageSummaries</c>.</summary>
public enum DateRangePreset
{
    Today,
    ThisWeek,
    ThisMonth,
    ThisYear,
    Custom
}
