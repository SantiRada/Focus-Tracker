using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace FocusTracker.Services;

public static class UrlReaderService
{
    // ── Browser registry ──────────────────────────────────────────────────
    private static readonly HashSet<string> ChromiumBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "operagx", "thorium", "arc", "chromium"
    };
    private static readonly HashSet<string> FirefoxBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "librewolf", "waterfox", "floorp"
    };

    public static bool IsBrowser(string processName) =>
        ChromiumBrowsers.Contains(processName) || FirefoxBrowsers.Contains(processName);

    // ── Win32 ─────────────────────────────────────────────────────────────
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildCallback cb, IntPtr lParam);

    private delegate bool EnumChildCallback(IntPtr hWnd, IntPtr lParam);

    // ── Public API ────────────────────────────────────────────────────────
    public static string? ReadUrlFromBrowser(IntPtr hwnd, string processName)
    {
        try
        {
            string? result = null;

            if (ChromiumBrowsers.Contains(processName))
                result = ReadChromiumUrl(hwnd, processName);
            else if (FirefoxBrowsers.Contains(processName))
                result = ReadFirefoxUrl(hwnd);

            LogDebug($"Browser={processName} → {result ?? "null"}");
            return result;
        }
        catch (Exception ex)
        {
            LogDebug($"ReadUrlFromBrowser({processName}): {ex.Message}");
            return null;
        }
    }

    // ── Strategy 1: deep recursive search for Chrome_OmniboxView ─────────
    private static string? ReadChromiumUrl(IntPtr rootHwnd, string processName)
    {
        // Strategy A: deep recursive EnumChildWindows
        var result = DeepFindOmnibox(rootHwnd);
        if (result != null)
        {
            LogDebug($"  [DeepEnum] found: '{result}'");
            return NormalizeHost(result);
        }

        // Strategy B: enumerate ALL top-level windows of this process,
        // then deep-search each one (Brave sometimes has the omnibox in a
        // separate top-level widget window)
        try
        {
            uint targetPid = 0;
            WinApi_GetWindowThreadProcessId(rootHwnd, out targetPid);

            if (targetPid > 0)
            {
                string? found = null;
                EnumChildWindows(IntPtr.Zero, (hwnd, _) =>
                {
                    if (found != null) return false;
                    WinApi_GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == targetPid)
                    {
                        var candidate = DeepFindOmnibox(hwnd);
                        if (candidate != null) { found = candidate; return false; }
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != null)
                {
                    LogDebug($"  [AllWindows] found: '{found}'");
                    return NormalizeHost(found);
                }
            }
        }
        catch { }

        // Strategy C: UI Automation (works on all Chromium browsers)
        return ReadChromiumViaUia(rootHwnd);
    }

    [DllImport("user32.dll")]
    private static extern uint WinApi_GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // Recursive deep search: EnumChildWindows only goes one level,
    // so we recurse manually
    private static string? DeepFindOmnibox(IntPtr parent)
    {
        string? found = null;

        EnumChildWindows(parent, (hwnd, _) =>
        {
            if (found != null) return false;

            var cls = new StringBuilder(128);
            GetClassName(hwnd, cls, 128);
            var clsStr = cls.ToString();

            // Log every unique class name we see (first pass only)
            LogDebug($"    class='{clsStr}'");

            if (clsStr == "Chrome_OmniboxView")
            {
                var sb = new StringBuilder(2048);
                GetWindowText(hwnd, sb, 2048);
                var text = sb.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    found = text;
                    return false;
                }
            }

            // Recurse into this child's children
            var sub = DeepFindOmnibox(hwnd);
            if (sub != null) { found = sub; return false; }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    // Strategy C: UI Automation on Chromium (fallback)
    private static string? ReadChromiumViaUia(IntPtr hwnd)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);

            // 1. Try by AutomationId (Edge: addressEditBox, Chrome: address-bar)
            var byId = new System.Windows.Automation.OrCondition(
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "addressEditBox"),
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "address-bar")
            );
            var el = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, byId);

            // 2. Try by ClassName
            if (el == null)
            {
                var byClass = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ClassNameProperty,
                    "Chrome_OmniboxView");
                el = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, byClass);
            }

            // 3. Try by ControlType=Edit and Name (common in older Chrome/Brave versions)
            if (el == null)
            {
                var byName = new System.Windows.Automation.AndCondition(
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit),
                    new System.Windows.Automation.OrCondition(
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Address and search bar"),
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Barra de direcciones y de búsqueda"),
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Address bar")
                    )
                );
                el = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, byName);
            }

            // 4. Final fallback: find any Edit control that looks like a URL
            if (el == null)
            {
                var anyEdit = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Edit);
                var edits = root.FindAll(System.Windows.Automation.TreeScope.Descendants, anyEdit);
                foreach (System.Windows.Automation.AutomationElement edit in edits)
                {
                    if (edit.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var p))
                    {
                        var v = ((System.Windows.Automation.ValuePattern)p).Current.Value;
                        if (v.Contains(".") && !v.Contains(" ") && v.Length > 3) { el = edit; break; }
                    }
                }
            }

            if (el == null) return null;

            if (el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var pat))
            {
                var val = ((System.Windows.Automation.ValuePattern)pat).Current.Value;
                LogDebug($"  [UIA-Chromium] value='{val}'");
                return NormalizeHost(val);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"  [UIA-Chromium] exception: {ex.Message}");
        }
        return null;
    }

    // ── Firefox: UI Automation ────────────────────────────────────────────
    private static string? ReadFirefoxUrl(IntPtr hwnd)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var condition = new System.Windows.Automation.AndCondition(
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.AutomationIdProperty, "urlbar-input"),
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Edit));

            var el = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, condition);
            if (el == null) return null;

            if (el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var pat))
            {
                var val = ((System.Windows.Automation.ValuePattern)pat).Current.Value;
                LogDebug($"  [Firefox-UIA] value='{val}'");
                return NormalizeHost(val);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"  [Firefox] exception: {ex.Message}");
        }
        return null;
    }

    // ── URL normalization ─────────────────────────────────────────────────
    public static string? NormalizeHost(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Contains(' ') && !raw.Contains('.')) return null;

        // Ensure we have a protocol for Uri.TryCreate to work correctly
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return null;
        
        // Remove 'www.' if it exists to normalize (tracked keys are also normalized)
        if (host.StartsWith("www.")) host = host[4..];
        
        return host;
    }

    // ── Logging ───────────────────────────────────────────────────────────
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FocusTracker", "url_debug.log");

    // Throttle: log at most once per 2 seconds to avoid huge files
    private static DateTime _lastLog = DateTime.MinValue;
    private static string _lastMsg = "";

    private static void LogDebug(string msg)
    {
        try
        {
            // Only log class names once per unique parent window to avoid spam
            if (msg.StartsWith("    class="))
            {
                if ((DateTime.Now - _lastLog).TotalSeconds < 5 && msg == _lastMsg) return;
                _lastLog = DateTime.Now;
                _lastMsg = msg;
            }
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}
