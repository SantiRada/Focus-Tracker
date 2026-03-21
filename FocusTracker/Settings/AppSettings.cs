using System.IO;
using System.Text.Json;

namespace FocusTracker.Settings;

public class AppSettings
{
    // ── Defaults ──────────────────────────────────────────────────────────
    private static readonly string _defaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusTracker");

    // Settings file is ALWAYS in %APPDATA%\FocusTracker — even if the DB moves elsewhere
    private static readonly string _settingsPath = Path.Combine(_defaultDir, "settings.json");

    // ── Properties ────────────────────────────────────────────────────────
    public string DataFolder          { get; set; } = _defaultDir;
    public bool   NotificationSound   { get; set; } = true;
    public bool   IdleDetection       { get; set; } = false;
    public int    IdleTimeoutSeconds  { get; set; } = 30;
    public bool   StartWithWindows    { get; set; } = true;
    public bool   MinimizeToTray      { get; set; } = true;

    /// <summary>
    /// When true, updates are downloaded and installed automatically without user confirmation.
    /// </summary>
    public bool   AutoUpdate          { get; set; } = false;

    /// <summary>
    /// Email last used successfully for a plugin license verification.
    /// Pre-fills the verification field so the user doesn't retype it each time.
    /// </summary>
    public string LicenseEmail { get; set; } = "";

    // ── Singleton ─────────────────────────────────────────────────────────
    private static AppSettings? _instance;
    public  static AppSettings  Instance => _instance ??= Load();

    // ── Persistence ───────────────────────────────────────────────────────
    private static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(_defaultDir);
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var s    = JsonSerializer.Deserialize<AppSettings>(json, opts);
                if (s != null)
                {
                    // Ensure the stored folder actually exists; fall back to default if not
                    if (!Directory.Exists(s.DataFolder))
                        s.DataFolder = _defaultDir;
                    return s;
                }
            }
        }
        catch { /* first run or corrupt file */ }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_defaultDir);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, opts));
        }
        catch { }
    }
}
