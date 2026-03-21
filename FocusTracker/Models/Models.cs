// TrackingSession, Project, TrackingItem, AppUsageSummary and DateRangePreset
// are defined in FocusTracker.PluginSDK (SdkModels.cs) so that plugins and
// the host share the exact same types from the same assembly.
// The using below re-exports them so all existing host code compiles unchanged.
global using FocusTracker.Models;

namespace FocusTracker.Models;

// ── Host-side only (not exposed to plugins) ───────────────────────────────────

public class InstalledApp
{
    public string  DisplayName    { get; set; } = "";
    public string  ExecutableName { get; set; } = "";
    public string? InstallLocation { get; set; }
    public bool    IsSelected     { get; set; }
    public bool    TrackUnfocus   { get; set; } = false;
    public override string ToString() => DisplayName;
}

public class TrackedUrl
{
    public string RawInput    { get; set; } = "";
    public string Host        { get; set; } = "";
    public string Label       { get; set; } = "";
    public bool   IsSelected  { get; set; } = true;
    public bool   TrackUnfocus { get; set; } = false;
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
