using System.Diagnostics;
using System.Linq;
using FocusTracker.Data;
using FocusTracker.Helpers;
using FocusTracker.Models;

namespace FocusTracker.Services;

public class TrackingService : IDisposable
{
    private readonly DatabaseService _db;
    private Thread?  _trackingThread;
    private volatile bool _isRunning;
    private int _currentSessionId;
    private int? _currentProjectId;
    private DateTime _startTime;

    // key → (displayName, trackUnfocus)
    private Dictionary<string, (string Label, bool TrackUnfocus)> _monitoredProcesses = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (string Label, bool TrackUnfocus)> _monitoredUrls      = new(StringComparer.OrdinalIgnoreCase);
    private bool _trackingUrls;

    // Focus state (app)
    private string?  _curProcName, _curProcDisplay;
    private DateTime _procFocusStart;

    // Focus state (url)
    private string?  _curUrlHost, _curUrlLabel;
    private DateTime _urlFocusStart;

    // Unfocus timers — track time when open-but-not-focused
    // key → (displayName, isUrl, unfocusStart, isCurrentlyUnfocused)
    private Dictionary<string, UnfocusState> _unfocusStates = new(StringComparer.OrdinalIgnoreCase);

    // Cached running processes for Unfocus tracking (refreshed every 5s)
    private HashSet<string> _runningProcessesCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRunningProcessRefresh = DateTime.MinValue;

    private record UnfocusState(string DisplayName, bool IsUrl, DateTime Start);

    public bool    IsTracking      => _isRunning;
    public int     CurrentSessionId => _currentSessionId;
    public string? CurrentFocusedApp => _curProcDisplay ?? _curUrlLabel;

    public int? SessionAlarmSeconds { get; set; }
    public int? TotalAlarmSeconds   { get; set; }
    public long InitialTotalSeconds { get; set; }

    private bool _sessionAlarmTriggered;
    private bool _totalAlarmTriggered;

    // ── Idle detection ────────────────────────────────────────────────────
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(30);
    /// <summary>When true, tracking will auto-stop if user idle exceeds 30 seconds.</summary>
    public bool IdleDetectionEnabled { get; set; }
    /// <summary>Fired on the tracking thread when idle threshold is exceeded. UI should call StopTracking.</summary>
    public event Action? IdleDetected;

    public event Action<string, TimeSpan>? FocusChanged;
    public event Action<string, string>?   AlarmTriggered;

    public TrackingService(DatabaseService db) => _db = db;

    // ── Start / Stop ──────────────────────────────────────────────────────
    public void StartTracking(
        List<InstalledApp> apps, List<TrackedUrl>? urls = null,
        string sessionName = "", int? projectId = null,
        int? sessionAlarmMins = null, int? totalAlarmMins = null)
    {
        if (_isRunning) return;

        _sessionAlarmTriggered = false;
        _totalAlarmTriggered   = false;
        SessionAlarmSeconds    = sessionAlarmMins * 60;
        TotalAlarmSeconds      = totalAlarmMins * 60;
        InitialTotalSeconds    = 0;

        if (projectId.HasValue)
        {
            // Get current total for this project to check against TotalAlarm
            var summaries = _db.GetUsageSummaries(DateTime.MinValue, DateTime.MaxValue, projectId.Value);
            InitialTotalSeconds = (long)summaries.Sum(s => s.FocusSeconds + s.UnfocusSeconds);
        }

        _monitoredProcesses = new Dictionary<string, (string Label, bool TrackUnfocus)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps.Where(a => !string.IsNullOrEmpty(a.ExecutableName)))
        {
            // Normalize: remove .exe extension if present
            var key = a.ExecutableName.ToLower();
            if (key.EndsWith(".exe")) key = key[..^4];
            
            if (!_monitoredProcesses.ContainsKey(key))
                _monitoredProcesses[key] = (a.DisplayName, a.TrackUnfocus);
        }

        _monitoredUrls = new Dictionary<string, (string Label, bool TrackUnfocus)>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in (urls ?? new()).Where(u => !string.IsNullOrEmpty(u.Host)))
        {
            if (!_monitoredUrls.ContainsKey(u.Host))
                _monitoredUrls[u.Host] = (u.Label, u.TrackUnfocus);
        }

        _trackingUrls = _monitoredUrls.Count > 0;
        _unfocusStates.Clear();

        var allTracked = _monitoredProcesses.Keys
            .Concat(_monitoredUrls.Keys.Select(h => $"[{h}]"));
        _startTime = DateTime.Now;
        _currentProjectId = projectId;
        _currentSessionId = _db.CreateSession(_startTime, allTracked, sessionName, projectId);

        _isRunning = true;
        _curProcName = null; _curUrlHost = null;

        _trackingThread = new Thread(TrackingLoop) { IsBackground = true, Name = "FocusTrackerThread" };
        _trackingThread.Start();
    }

    public void StopTracking()
    {
        if (!_isRunning) return;
        _isRunning = false;
        var now = DateTime.Now;

        // Flush focused blocks
        FlushFocus(now);

        // Flush any open unfocus blocks
        foreach (var (key, state) in _unfocusStates)
        {
            var dur = now - state.Start;
            if (dur.TotalSeconds >= 1)
                _db.InsertFocusEvent(_currentSessionId, key, state.DisplayName,
                    state.Start, now, state.IsUrl, isUnfocus: true);
        }
        _unfocusStates.Clear();
        _db.CloseSession(_currentSessionId, now);
    }

    public void AddProcess(InstalledApp app)
    {
        if (!_isRunning || string.IsNullOrEmpty(app.ExecutableName)) return;
        var key = app.ExecutableName.ToLower();
        if (key.EndsWith(".exe")) key = key[..^4];
        _monitoredProcesses[key] = (app.DisplayName, app.TrackUnfocus);
    }
    public void AddUrl(TrackedUrl url)
    {
        if (!_isRunning || string.IsNullOrEmpty(url.Host)) return;
        _monitoredUrls[url.Host] = (url.Label, url.TrackUnfocus);
        _trackingUrls = true;
    }

    // ── Loop ──────────────────────────────────────────────────────────────
    private int _flushCounter = 0;

    private void TrackingLoop()
    {
        while (_isRunning)
        {
            try
            {
                // Idle detection — check before Tick so we stop cleanly
                if (IdleDetectionEnabled && WinApi.GetIdleTime() >= IdleThreshold)
                {
                    IdleDetected?.Invoke();
                    break; // exit loop; StopTracking will be called by the UI handler
                }

                Tick();
                if (++_flushCounter >= 2) // Every 1 second (500ms * 2)
                {
                    _flushCounter = 0;
                    PeriodicFlush(DateTime.Now);
                }
            }
            catch { }
            Thread.Sleep(500);
        }
    }

    private void PeriodicFlush(DateTime now)
    {
        if (!_isRunning) return;

        // Flush active focus blocks but KEEP them active by resetting start time
        if (_curProcName != null)
        {
            var dur = now - _procFocusStart;
            if (dur.TotalSeconds >= 1)
            {
                _db.InsertFocusEvent(_currentSessionId, _curProcName, _curProcDisplay!, _procFocusStart, now, false, false);
                _procFocusStart = now;
            }
        }
        if (_curUrlHost != null)
        {
            var dur = now - _urlFocusStart;
            if (dur.TotalSeconds >= 1)
            {
                _db.InsertFocusEvent(_currentSessionId, _curUrlHost, _curUrlLabel!, _urlFocusStart, now, true, false);
                _urlFocusStart = now;
            }
        }

        // Flush active unfocus blocks but KEEP them active
        var unfocusKeys = _unfocusStates.Keys.ToList();
        foreach (var key in unfocusKeys)
        {
            var state = _unfocusStates[key];
            var dur = now - state.Start;
            if (dur.TotalSeconds >= 1)
            {
                _db.InsertFocusEvent(_currentSessionId, key, state.DisplayName, state.Start, now, state.IsUrl, isUnfocus: true);
                _unfocusStates[key] = state with { Start = now };
            }
        }
    }

    private void Tick()
    {
        var (hwnd, pid, _) = WinApi.GetForegroundProcessInfo();
        var now = DateTime.Now;

        string? focusedProcName = null, focusedProcDisplay = null;
        bool    isBrowser = false;

        if (pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName.ToLower();
                isBrowser = UrlReaderService.IsBrowser(name);
                
                // We always identify the current process for Unfocus tracking
                string? currentProcName = name;

                // Prioritize URL matching if it's a browser
                string? focusedUrlHost = null, focusedUrlLabel = null;
                if (_trackingUrls && isBrowser && hwnd != IntPtr.Zero)
                {
                    var rawHost = UrlReaderService.ReadUrlFromBrowser(hwnd, name);
                    if (rawHost != null)
                    {
                        foreach (var (host, info) in _monitoredUrls)
                        {
                            if (HostMatches(rawHost, host)) 
                            { 
                                focusedUrlHost = host; 
                                focusedUrlLabel = info.Label; 
                                break; 
                            }
                        }
                    }
                }

                // If a URL is focused, we treat it as the primary focus
                if (focusedUrlHost != null)
                {
                    HandleUrlFocusChange(focusedUrlHost, focusedUrlLabel, now);
                    // Clear app focus to avoid double tracking
                    HandleAppFocusChange(null, null, now);
                }
                else
                {
                    // No URL focused, check if the app itself is monitored
                    if (_monitoredProcesses.TryGetValue(name, out var info))
                    {
                        focusedProcName    = name;
                        focusedProcDisplay = info.Label;
                    }
                    HandleAppFocusChange(focusedProcName, focusedProcDisplay, now);
                    HandleUrlFocusChange(null, null, now);
                }

                // Unfocus tracking - pass the ACTUAL process name even if it's a browser with focused URL
                // so the browser app doesn't count as "unfocused" while a URL is "focused"
                HandleUnfocusTracking(currentProcName, focusedUrlHost, now);
            }
            catch { }
        }
        else
        {
            // Nothing focused
            HandleAppFocusChange(null, null, now);
            HandleUrlFocusChange(null, null, now);
            HandleUnfocusTracking(null, null, now);
        }

        CheckAlarms();
    }

    private void CheckAlarms()
    {
        if (!_isRunning) return;

        var elapsed = DateTime.Now - _startTime;
        var elapsedSecs = (long)elapsed.TotalSeconds;

        // Session alarm
        if (SessionAlarmSeconds.HasValue && elapsedSecs >= SessionAlarmSeconds.Value && !_sessionAlarmTriggered)
        {
            _sessionAlarmTriggered = true;
            AlarmTriggered?.Invoke("Alarma de sesión", $"Llevás {elapsedSecs / 60} minutos en esta sesión.");
        }

        // Total alarm (only if project is set)
        if (TotalAlarmSeconds.HasValue && _currentProjectId.HasValue && !_totalAlarmTriggered)
        {
            // Total = Initial + Current session elapsed
            var totalSecs = InitialTotalSeconds + elapsedSecs;
            if (totalSecs >= TotalAlarmSeconds.Value)
            {
                _totalAlarmTriggered = true;
                AlarmTriggered?.Invoke("Alarma de proyecto", $"El proyecto alcanzó el límite de {TotalAlarmSeconds.Value / 60} minutos totales.");
            }
        }
    }

    private string GetProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.ToLower(); }
        catch { return ""; }
    }

    private void HandleUnfocusTracking(string? focusedProc, string? focusedUrl, DateTime now)
    {
        // Refresh running processes list every 5 seconds to avoid high CPU usage
        if ((now - _lastRunningProcessRefresh).TotalSeconds >= 5)
        {
            _runningProcessesCache.Clear();
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    _runningProcessesCache.Add(proc.ProcessName.ToLower());
                    proc.Dispose();
                }
            }
            catch { }
            _lastRunningProcessRefresh = now;
        }

        // Apps
        foreach (var (key, info) in _monitoredProcesses)
        {
            if (!info.TrackUnfocus) continue;
            bool isRunning  = _runningProcessesCache.Contains(key);
            bool isFocused  = string.Equals(key, focusedProc, StringComparison.OrdinalIgnoreCase);
            bool wasUnfocus = _unfocusStates.ContainsKey(key);

            if (isRunning && !isFocused)
            {
                if (!wasUnfocus) _unfocusStates[key] = new UnfocusState(info.Label, false, now);
            }
            else
            {
                if (wasUnfocus) FlushUnfocus(key, now);
            }
        }

        // URLs — only relevant if a browser is open
        foreach (var (host, info) in _monitoredUrls)
        {
            if (!info.TrackUnfocus) continue;
            bool isFocused  = string.Equals(host, focusedUrl, StringComparison.OrdinalIgnoreCase);
            
            // Refined URL "open" check:
            // We only consider a URL "open" if it was VERY recently seen as focused
            // or if we can find it in the window titles of the browser.
            // This prevents "ghost" unfocus time when the URL isn't actually loaded in any tab.
            bool isActuallyOpen = isFocused || IsUrlInAnyWindowTitle(host);
            
            bool wasUnfocus = _unfocusStates.ContainsKey(host);

            if (isActuallyOpen && !isFocused)
            {
                if (!wasUnfocus) _unfocusStates[host] = new UnfocusState(info.Label, true, now);
            }
            else
            {
                if (wasUnfocus) FlushUnfocus(host, now);
            }
        }
    }

    private bool IsUrlInAnyWindowTitle(string host)
    {
        try
        {
            // Scan all top-level windows for titles containing the host
            // (e.g. "YouTube" or "youtube.com" in Chrome/Edge titles)
            var titles = new List<string>();
            WinApi.EnumWindows((hwnd, _) => {
                if (WinApi.IsWindowVisible(hwnd))
                {
                    var sb = new System.Text.StringBuilder(256);
                    WinApi.GetWindowText(hwnd, sb, 256);
                    var title = sb.ToString();
                    if (!string.IsNullOrEmpty(title)) titles.Add(title.ToLower());
                }
                return true;
            }, IntPtr.Zero);

            return titles.Any(t => t.Contains(host.ToLower()));
        }
        catch { return false; }
    }

    private void FlushUnfocus(string key, DateTime now)
    {
        if (!_unfocusStates.TryGetValue(key, out var state)) return;
        _unfocusStates.Remove(key);
        var dur = now - state.Start;
        if (dur.TotalSeconds >= 1)
            _db.InsertFocusEvent(_currentSessionId, key, state.DisplayName,
                state.Start, now, state.IsUrl, isUnfocus: true);
    }

    // ── Focus handlers ────────────────────────────────────────────────────
    private void HandleAppFocusChange(string? newProc, string? newDisplay, DateTime now)
    {
        if (string.Equals(newProc, _curProcName, StringComparison.OrdinalIgnoreCase)) return;
        if (_curProcName != null)
        {
            var dur = now - _procFocusStart;
            if (dur.TotalSeconds >= 1)
                _db.InsertFocusEvent(_currentSessionId, _curProcName, _curProcDisplay!, _procFocusStart, now, false, false);
        }
        _curProcName    = newProc;
        _curProcDisplay = newDisplay;
        if (newProc != null) { _procFocusStart = now; FocusChanged?.Invoke(newDisplay!, TimeSpan.Zero); }
    }

    private void HandleUrlFocusChange(string? newHost, string? newLabel, DateTime now)
    {
        if (string.Equals(newHost, _curUrlHost, StringComparison.OrdinalIgnoreCase)) return;
        if (_curUrlHost != null)
        {
            var dur = now - _urlFocusStart;
            if (dur.TotalSeconds >= 1)
                _db.InsertFocusEvent(_currentSessionId, _curUrlHost, _curUrlLabel!, _urlFocusStart, now, true, false);
        }
        _curUrlHost  = newHost;
        _curUrlLabel = newLabel;
        if (newHost != null) { _urlFocusStart = now; FocusChanged?.Invoke(newLabel!, TimeSpan.Zero); }
    }

    private void FlushFocus(DateTime now)
    {
        if (_curProcName != null)
        {
            var dur = now - _procFocusStart;
            if (dur.TotalSeconds >= 1)
                _db.InsertFocusEvent(_currentSessionId, _curProcName, _curProcDisplay!, _procFocusStart, now, false, false);
            _curProcName = null;
        }
        if (_curUrlHost != null)
        {
            var dur = now - _urlFocusStart;
            if (dur.TotalSeconds >= 1)
                _db.InsertFocusEvent(_currentSessionId, _curUrlHost, _curUrlLabel!, _urlFocusStart, now, true, false);
            _curUrlHost = null;
        }
    }

    private static bool HostMatches(string active, string tracked)
    {
        if (string.IsNullOrWhiteSpace(active) || string.IsNullOrWhiteSpace(tracked)) return false;
        
        active = active.ToLowerInvariant();
        tracked = tracked.ToLowerInvariant();

        if (active == tracked) return true;

        // Check if tracked is a parent domain of active
        // e.g. active="mail.google.com", tracked="google.com" -> true
        // e.g. active="google.com.ar", tracked="google.com" -> true (if we want to allow regional)
        // To be safe, we check if active ends with "." + tracked
        if (active.EndsWith("." + tracked)) return true;

        // Special case for regional domains: if tracked is "google.com" and active is "google.com.ar"
        // We can split and check if the main parts match.
        // For now, let's keep it simple: if the tracked string is contained in the active string 
        // AND it's surrounded by dots or at the start/end.
        var parts = active.Split('.');
        var trackedParts = tracked.Split('.');
        
        // If tracked has 2+ parts (like google.com), check if those parts appear consecutively in active
        if (trackedParts.Length >= 2)
        {
            for (int i = 0; i <= parts.Length - trackedParts.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < trackedParts.Length; j++)
                {
                    if (parts[i + j] != trackedParts[j]) { match = false; break; }
                }
                if (match) return true;
            }
        }

        return false;
    }

    public void Dispose() => StopTracking();
}
