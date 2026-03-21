using System.Runtime.InteropServices;
using System.Text;

namespace FocusTracker.Helpers;

public static class WinApi
{
    // ── Idle / last input detection ───────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Returns time elapsed since the last keyboard or mouse input.</summary>
    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    // ── Window / process ─────────────────────────────────────────────────
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ── Foreground window control ─────────────────────────────────────────
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>nCmdShow values for ShowWindow.</summary>
    public const int SW_RESTORE  = 9;
    public const int SW_SHOW     = 5;
    public const int SW_SHOWNA   = 8; // show without activating (useful as pre-step)

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // DWM dark mode title bar (Windows 10 20H1+ / Windows 11)
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int value = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { /* older Windows — silently ignore */ }
    }

    public static (IntPtr hwnd, uint processId, string windowTitle) GetForegroundProcessInfo()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (IntPtr.Zero, 0, "");
        GetWindowThreadProcessId(hwnd, out uint pid);
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, 256);
        return (hwnd, pid, sb.ToString());
    }
}
