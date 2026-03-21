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

    // URLs that were actually focused during this session; used for reliable unfocus detection.
    // (IsUrlInAnyWindowTitle is unreliable because Chrome titles show page titles, not domains.)
    private readonly HashSet<string> _urlsEverFocused = new(StringComparer.OrdinalIgnoreCase);

    // Last raw browser URL dispatched to plugins via FocusChanged (no DB recording).
    // Kept separate from _curUrlHost so non-project URLs don't pollute project tracking.
    private string? _rawBrowserFocusForPlugins;

    // URL result cache — reused for up to 3 s when UIA fails intermittently,
    // so brief read failures don't interrupt an active project URL tracking block.
    private string?  _lastConfirmedBrowserUrl;
    private uint     _lastConfirmedBrowserPid;
    private DateTime _lastConfirmedBrowserTime = DateTime.MinValue;
    private static readonly TimeSpan UrlCacheMaxAge = TimeSpan.FromSeconds(3);

    private record UnfocusState(string DisplayName, bool IsUrl, DateTime Start);

    public bool    IsTracking      => _isRunning;
    public int     CurrentSessionId => _currentSessionId;
    public string? CurrentFocusedApp => _curProcDisplay ?? _curUrlLabel;

    // ── Pause / Resume ────────────────────────────────────────────────────
    private volatile bool _isPaused = false;
    /// <summary>True when tracking is running but temporarily paused (e.g. after idle detection).</summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Flushes open focus/unfocus blocks to the DB and suspends ticking.
    /// The session stays open — call ResumeTracking to continue.
    /// </summary>
    public void PauseTracking()
    {
        if (!_isRunning || _isPaused) return;
        _isPaused = true;

        var now = DateTime.Now;
        // Flush focused block without closing it permanently
        FlushFocus(now);

        // Flush open unfocus blocks
        foreach (var (key, state) in _unfocusStates)
        {
            var dur = now - state.Start;
            if (dur.TotalSeconds >= 1)
                _db.InsertFocusEvent(_currentSessionId, key, state.DisplayName,
                    state.Start, now, state.IsUrl, isUnfocus: true);
        }
        _unfocusStates.Clear();

        // Reset focus state so resume starts clean
        _curProcName    = null;
        _curProcDisplay = null;
        _curUrlHost     = null;
        _curUrlLabel    = null;
    }

    /// <summary>Resumes a paused session. No-op if not paused.</summary>
    public void ResumeTracking()
    {
        if (!_isRunning || !_isPaused) return;
        var now = DateTime.Now;
        _procFocusStart = now;
        _urlFocusStart  = now;
        _isPaused = false;
    }

    public int? SessionAlarmSeconds { get; set; }
    public int? TotalAlarmSeconds   { get; set; }
    public long InitialTotalSeconds { get; set; }

    private bool _sessionAlarmTriggered;
    private bool _totalAlarmTriggered;

    // ── Idle detection ────────────────────────────────────────────────────
    /// <summary>Seconds of inactivity before auto-pause. Default 30.</summary>
    public int  IdleTimeoutSeconds   { get; set; } = 30;
    private TimeSpan IdleThreshold => TimeSpan.FromSeconds(IdleTimeoutSeconds);
    /// <summary>When true, tracking will auto-pause if user idle exceeds IdleTimeoutSeconds.</summary>
    public bool IdleDetectionEnabled { get; set; }
    /// <summary>Fired on the tracking thread when idle threshold is exceeded. UI should call PauseTracking.</summary>
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
        _urlsEverFocused.Clear();
        _rawBrowserFocusForPlugins  = null;
        _lastConfirmedBrowserUrl    = null;
        _lastConfirmedBrowserPid    = 0;
        _lastConfirmedBrowserTime   = DateTime.MinValue;

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
        _isPaused  = false;
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
                // While paused: keep the loop alive but skip all tracking logic
                if (_isPaused)
                {
                    Thread.Sleep(500);
                    continue;
                }

                // Idle detection — pause from within the loop so the thread stays alive
                if (IdleDetectionEnabled && WinApi.GetIdleTime() >= IdleThreshold)
                {
                    PauseTracking();        // flush open blocks and set _isPaused = true
                    IdleDetected?.Invoke(); // notify UI (dispatched to UI thread)
                    continue;              // stay in the while loop; _isPaused branch handles sleep
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
        var (hwnd, pid, windowTitle) = WinApi.GetForegroundProcessInfo();
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

                // ── Step 1: try to read the browser URL ───────────────────────────
                // Always runs when a browser is focused, regardless of _trackingUrls.
                string? focusedUrlHost = null, focusedUrlLabel = null;
                string? rawBrowserHost = null;  // for plugin notification only (no DB)

                if (isBrowser && hwnd != IntPtr.Zero)
                {
                    var rawHost = UrlReaderService.ReadUrlFromBrowser(hwnd, name);

                    if (rawHost != null)
                    {
                        // Successful read — update the cache
                        _lastConfirmedBrowserUrl  = rawHost;
                        _lastConfirmedBrowserPid  = pid;
                        _lastConfirmedBrowserTime = now;

                        rawBrowserHost = rawHost;

                        // Match against project-monitored URLs → DB recording + label
                        if (_trackingUrls)
                        {
                            foreach (var (host, info) in _monitoredUrls)
                            {
                                if (HostMatches(rawHost, host))
                                {
                                    focusedUrlHost  = host;
                                    focusedUrlLabel = info.Label;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        // UIA read failed. Try the URL cache first — it covers brief
                        // intermittent timeouts without interrupting an active tracking block.
                        if (_lastConfirmedBrowserPid == pid &&
                            now - _lastConfirmedBrowserTime <= UrlCacheMaxAge &&
                            _lastConfirmedBrowserUrl != null)
                        {
                            rawBrowserHost = _lastConfirmedBrowserUrl;

                            if (_trackingUrls)
                            {
                                foreach (var (host, info) in _monitoredUrls)
                                {
                                    if (HostMatches(_lastConfirmedBrowserUrl, host))
                                    {
                                        focusedUrlHost  = host;
                                        focusedUrlLabel = info.Label;
                                        break;
                                    }
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(windowTitle))
                        {
                            // Cache expired or different window.
                            // Fall back to window-title matching. Strategy:
                            //   "YouTube - Google Chrome" → title contains "youtube"
                            //   "Stack Overflow - Google Chrome" → title contains "stackoverflow"
                            // We check each monitored URL's domain parts against the whole
                            // title instead of trying to extract a single "site hint" first —
                            // this handles multi-word site names and complex title formats.
                            var titleLower = windowTitle.ToLowerInvariant();

                            // Best-effort plugin notification hint (second-to-last segment).
                            var tParts    = windowTitle.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                            var titleHint = tParts.Length >= 2
                                ? tParts[^2].Trim().ToLowerInvariant()
                                : tParts[0].Trim().ToLowerInvariant();
                            if (!string.IsNullOrEmpty(titleHint))
                                rawBrowserHost = titleHint;

                            // Try to match a project URL against the window title.
                            if (_trackingUrls)
                            {
                                foreach (var (host, info) in _monitoredUrls)
                                {
                                    // 1. Full host in title ("youtube.com" in "youtube.com - chrome")
                                    if (titleLower.Contains(host.ToLowerInvariant()))
                                    {
                                        focusedUrlHost  = host;
                                        focusedUrlLabel = info.Label;
                                        break;
                                    }

                                    // 2. Any meaningful domain segment in title.
                                    //    "youtube.com"      → test "youtube" (length ≥ 3)
                                    //    "stackoverflow.com" → test "stackoverflow"
                                    //    Skip short TLD parts like "com", "net", "co".
                                    var hostParts = host.ToLowerInvariant().Split('.');
                                    bool matched  = false;
                                    foreach (var part in hostParts)
                                    {
                                        if (part.Length >= 4 && titleLower.Contains(part))
                                        {
                                            matched = true;
                                            break;
                                        }
                                    }
                                    if (matched)
                                    {
                                        focusedUrlHost  = host;
                                        focusedUrlLabel = info.Label;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                // ── Step 2: project-URL focus (recorded to DB) ────────────────────
                if (focusedUrlHost != null)
                {
                    HandleUrlFocusChange(focusedUrlHost, focusedUrlLabel, now);
                    HandleAppFocusChange(null, null, now);
                    // Project URL matched → plugins get the label via HandleUrlFocusChange above;
                    // reset the raw-browser cache so it re-fires if the user later leaves the project URL.
                    _rawBrowserFocusForPlugins = null;
                }
                else
                {
                    // No project URL matched
                    if (_monitoredProcesses.TryGetValue(name, out var info))
                    {
                        focusedProcName    = name;
                        focusedProcDisplay = info.Label;
                    }
                    HandleAppFocusChange(focusedProcName, focusedProcDisplay, now);
                    HandleUrlFocusChange(null, null, now);

                    // ── Step 3: plugin-only notification (no DB) ───────────────────
                    // Fire FocusChanged with the raw browser URL so plugins like
                    // UseLimitPlugin can detect websites that aren't in any project.
                    if (!string.Equals(rawBrowserHost, _rawBrowserFocusForPlugins,
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        _rawBrowserFocusForPlugins = rawBrowserHost;
                        if (rawBrowserHost != null)
                            FocusChanged?.Invoke(rawBrowserHost, TimeSpan.Zero);
                    }
                }

                // Unfocus tracking — pass the project URL host (not the raw host) so the
                // string.Equals check inside HandleUnfocusTracking matches project keys.
                HandleUnfocusTracking(currentProcName, focusedUrlHost, now);
            }
            catch { }
        }
        else
        {
            // Nothing focused
            _rawBrowserFocusForPlugins = null;
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
        // A URL is considered "still open" if it was visited during this session
        // AND at least one browser process is currently running.
        // (IsUrlInAnyWindowTitle is unreliable: Chrome titles show page titles, not domains.)
        bool anyBrowserRunning = _runningProcessesCache.Any(p => UrlReaderService.IsBrowser(p));

        foreach (var (host, info) in _monitoredUrls)
        {
            if (!info.TrackUnfocus) continue;
            bool isFocused      = string.Equals(host, focusedUrl, StringComparison.OrdinalIgnoreCase);
            bool isActuallyOpen = isFocused || (_urlsEverFocused.Contains(host) && anyBrowserRunning);

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
        if (newHost != null)
        {
            _urlsEverFocused.Add(newHost);  // remember for unfocus detection
            _urlFocusStart = now;
            FocusChanged?.Invoke(newLabel!, TimeSpan.Zero);
        }
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

        active  = active.ToLowerInvariant();
        tracked = tracked.ToLowerInvariant();

        // Exact match
        if (active == tracked) return true;

        // Subdomain match: active="mail.google.com", tracked="google.com" → true
        if (active.EndsWith("." + tracked)) return true;

        var activeParts  = active.Split('.');
        var trackedParts = tracked.Split('.');

        // Single-part tracked key (no TLD stored, e.g. user entered "youtube" not "youtube.com").
        // Match if any label of the active host equals the tracked name.
        // "youtube.com" vs "youtube" → activeParts contains "youtube" → true
        // "mail.google.com" vs "google"  → activeParts contains "google"  → true
        if (trackedParts.Length == 1)
            return activeParts.Any(p => p == tracked);

        // Single-part active (UIA returned a bare hostname) vs multi-part tracked.
        // "youtube" vs "youtube.com" → tracked starts with "youtube." → true
        if (activeParts.Length == 1)
            return tracked.StartsWith(active + ".");

        // Multi-part tracked key: check if tracked parts appear consecutively inside active.
        // "google.com.ar" vs "google.com" → consecutive match → true
        for (int i = 0; i <= activeParts.Length - trackedParts.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < trackedParts.Length; j++)
            {
                if (activeParts[i + j] != trackedParts[j]) { match = false; break; }
            }
            if (match) return true;
        }

        return false;
    }

    public void Dispose() => StopTracking();
}
