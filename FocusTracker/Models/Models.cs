namespace FocusTracker.Models;

public class InstalledApp
{
    public string DisplayName { get; set; } = "";
    public string ExecutableName { get; set; } = "";
    public string? InstallLocation { get; set; }
    public bool IsSelected { get; set; }
    public bool TrackUnfocus { get; set; } = false;
    public override string ToString() => DisplayName;
}

public class TrackedUrl
{
    public string RawInput { get; set; } = "";
    public string Host     { get; set; } = "";
    public string Label    { get; set; } = "";
    public bool IsSelected { get; set; } = true;
    public bool TrackUnfocus { get; set; } = false;
}

// Unified item for the "selected" chips list
public class TrackingItem
{
    public string Key         { get; set; } = ""; // processName or host
    public string Label       { get; set; } = "";
    public bool   IsUrl       { get; set; }
    public bool   TrackUnfocus { get; set; }
}

public class Project
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string TrackedKeys { get; set; } = ""; // comma-separated processName/host
    public int?   TotalTimeAlarmSeconds   { get; set; }
    public int?   SessionTimeAlarmSeconds { get; set; }
    // runtime-only
    public List<TrackingItem> Items { get; set; } = new();
}

public class TrackingSession
{
    public int     Id               { get; set; }
    public string  Name             { get; set; } = "";
    public DateTime StartTime       { get; set; }
    public DateTime? EndTime        { get; set; }
    public string  TrackedProcesses { get; set; } = "";
    public int?    ProjectId        { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Sesión #{Id}" : Name;
}

public class FocusEvent
{
    public int      Id             { get; set; }
    public int      SessionId      { get; set; }
    public string   ProcessName    { get; set; } = "";
    public string   AppDisplayName { get; set; } = "";
    public DateTime StartTime      { get; set; }
    public DateTime EndTime        { get; set; }
    public bool     IsUrl          { get; set; }
    public bool     IsUnfocus      { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}

public class AppUsageSummary
{
    public string   ProcessName    { get; set; } = "";
    public string   AppDisplayName { get; set; } = "";
    public long     FocusSeconds   { get; set; }
    public long     UnfocusSeconds { get; set; }
    public int      EventCount     { get; set; }
    public bool     IsUrl          { get; set; }
    public bool     IsProjectExtra { get; set; }

    // Convenience
    public TimeSpan FocusTime   => TimeSpan.FromSeconds(FocusSeconds);
    public TimeSpan UnfocusTime => TimeSpan.FromSeconds(UnfocusSeconds);
    public TimeSpan TotalTime   => TimeSpan.FromSeconds(FocusSeconds + UnfocusSeconds);

    public string FormattedTime
    {
        get
        {
            var ts = TotalTime;
            if (ts.TotalHours >= 1)   return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }
}

public enum DateRangePreset { Today, ThisWeek, ThisMonth, ThisYear, Custom }
