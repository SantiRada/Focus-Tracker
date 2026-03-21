using FocusTracker.Models;
using WpfBrush      = System.Windows.Media.Brush;
using WpfSolidBrush = System.Windows.Media.SolidColorBrush;
using WpfColor      = System.Windows.Media.Color;

namespace FocusTracker.ViewModels;

public class SessionViewModel
{
    private readonly TrackingSession _s;
    public SessionViewModel(TrackingSession s) => _s = s;

    public int    Id          => _s.Id;
    public string DisplayName => _s.DisplayName;
    public int?   ProjectId   => _s.ProjectId;
    public TrackingSession Raw => _s;

    public string DurationDisplay
    {
        get
        {
            var dur = (_s.EndTime ?? DateTime.Now) - _s.StartTime;
            if (dur.TotalHours >= 1) return $"{(int)dur.TotalHours}h {dur.Minutes}m";
            return $"{dur.Minutes}m {dur.Seconds}s";
        }
    }
    public string StatusDisplay => _s.EndTime.HasValue ? "Completada" : "Activa";
    public string DateDisplay   => _s.StartTime.ToString("dd/MM/yyyy");
}

// One row per app — may contain both focus and unfocus time
public class UsageSummaryViewModel
{
    public string   ProcessName    { get; set; } = "";
    public string   AppDisplayName { get; set; } = "";
    public TimeSpan FocusTime      { get; set; }
    public TimeSpan UnfocusTime    { get; set; }
    public int      EventCount     { get; set; }
    public double   TotalPercent   { get; set; }
    public bool     IsUrl          { get; set; }
    public bool     IsProjectExtra { get; set; }

    public bool HasUnfocus => UnfocusTime > TimeSpan.Zero;

    // Total = focus + unfocus
    public TimeSpan TotalTime => FocusTime + UnfocusTime;

    public string FormattedFocus   => Format(FocusTime);
    public string FormattedUnfocus => HasUnfocus ? Format(UnfocusTime) : "";
    public string FormattedTotal   => Format(TotalTime);

    // For simple display (tables that show one time column)
    public string FormattedTime => HasUnfocus
        ? $"{Format(FocusTime)} focus"
        : Format(FocusTime);

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)   return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    public string PercentLabel => TotalPercent > 0 ? $"{TotalPercent:0.0}%" : "—";

    public string BadgeLabel    => IsUrl ? "URL" : "APP";
    public WpfBrush BadgeBackground => IsUrl ? Hex("#1A2A44") : Hex("#1A2200");
    public WpfBrush BadgeBorder     => IsUrl ? Hex("#2A4A88") : Hex("#4A6600");
    public WpfBrush BadgeForeground => IsUrl ? Hex("#60AAFF") : Hex("#C8FF00");

    // Unfocus badge colors (orange)
    public WpfBrush UnfocusBg  => Hex("#2A1A00");
    public WpfBrush UnfocusBdr => Hex("#664400");
    public WpfBrush UnfocusFg  => Hex("#FFAA44");

    private static WpfSolidBrush Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return new WpfSolidBrush(WpfColor.FromRgb(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16)));
    }
}

public class ProjectViewModel
{
    public Project Raw { get; }
    public ProjectViewModel(Project p) => Raw = p;
    public int    Id           => Raw.Id;
    public string Name         => Raw.Name;
    public int    ItemCount    => Raw.TrackedKeys
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Count(k => !k.StartsWith("[opt:"));
    public bool   UsePomodoro  => Raw.UsePomodoro;
    // Set by LoadProjects so Comenzar is disabled during active tracking
    public bool CanStart { get; set; } = true;
}
