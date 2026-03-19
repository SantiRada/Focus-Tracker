using Microsoft.Win32;
using FocusTracker.Models;
using System.Diagnostics;
using System.IO;

namespace FocusTracker.Helpers;

public static class InstalledAppsHelper
{
    // Registry paths (system + user + WOW64)
    private static readonly string[] UninstallKeys = {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    // Common %LocalAppData% app folders (Squirrel-installed apps like Claude, Discord, etc.)
    private static readonly string[] LocalAppSubFolders = {
        "Claude", "Discord", "Slack", "Figma", "Notion", "Postman",
        "Microsoft VS Code", "GitHubDesktop", "Obsidian", "Linear",
        "Loom", "Zoom", "WhatsApp", "Telegram Desktop", "signal-desktop",
        "1Password 7 - Password Manager", "Bitwarden"
    };

    public static List<InstalledApp> GetInstalledApps()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // 1. HKLM registry (machine-wide installs)
        ScanRegistryHive(Registry.LocalMachine, UninstallKeys[0], apps);
        ScanRegistryHive(Registry.LocalMachine, UninstallKeys[1], apps);

        // 2. HKCU registry (per-user installs, including Squirrel apps)
        ScanRegistryHive(Registry.CurrentUser, UninstallKeys[0], apps);

        // 3. Explicit scan of %LocalAppData% for common Squirrel-installed apps
        ScanLocalAppData(apps);

        // 4. Scan all running processes — always include what's open right now
        MergeRunningProcesses(apps);

        // 5. Well-known apps (fills missing exe names or adds missing entries)
        AddWellKnownApps(apps);

        return apps.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
            .OrderBy(a => a.DisplayName)
            .ToList();
    }

    private static void ScanRegistryHive(RegistryKey hive, string keyPath,
        Dictionary<string, InstalledApp> apps)
    {
        using var baseKey = hive.OpenSubKey(keyPath);
        if (baseKey == null) return;

        foreach (var subKeyName in baseKey.GetSubKeyNames())
        {
            using var subKey = baseKey.OpenSubKey(subKeyName);
            if (subKey == null) continue;

            var displayName = subKey.GetValue("DisplayName")?.ToString();
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            // Skip system components and updates
            var systemComponent = subKey.GetValue("SystemComponent");
            if (systemComponent is int sc && sc == 1) continue;
            var releaseType = subKey.GetValue("ReleaseType")?.ToString();
            if (!string.IsNullOrEmpty(releaseType)) continue;

            var installLocation = subKey.GetValue("InstallLocation")?.ToString();
            var displayIcon     = subKey.GetValue("DisplayIcon")?.ToString();

            var exeName = TryResolveExecutable(installLocation, displayIcon, displayName);

            if (!apps.ContainsKey(displayName))
                apps[displayName] = new InstalledApp
                {
                    DisplayName    = displayName,
                    ExecutableName = exeName ?? "",
                    InstallLocation = installLocation
                };
            else if (!string.IsNullOrEmpty(exeName) && string.IsNullOrEmpty(apps[displayName].ExecutableName))
                apps[displayName].ExecutableName = exeName;
        }
    }

    // Scan %LocalAppData% for Squirrel/electron apps (Claude, Discord new builds, etc.)
    private static void ScanLocalAppData(Dictionary<string, InstalledApp> apps)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Directory.Exists(localApp)) return;

        // Strategy 1: known folder names
        foreach (var folder in LocalAppSubFolders)
        {
            var dir = Path.Combine(localApp, folder);
            TryScanAppDir(dir, folder, apps);
        }

        // Strategy 2: scan all immediate subdirectories looking for a current/app-*.exe pattern
        // (Squirrel pattern: AppName\current\AppName.exe)
        try
        {
            foreach (var dir in Directory.GetDirectories(localApp))
            {
                var folderName = Path.GetFileName(dir);
                if (folderName.StartsWith(".") || folderName.Equals("Temp", StringComparison.OrdinalIgnoreCase))
                    continue;

                var currentDir = Path.Combine(dir, "current");
                if (Directory.Exists(currentDir))
                    TryScanAppDir(currentDir, folderName, apps);
            }
        }
        catch { }
    }

    private static void TryScanAppDir(string dir, string folderName,
        Dictionary<string, InstalledApp> apps)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            // Try "current" subfolder first (Squirrel layout)
            var currentDir = Path.Combine(dir, "current");
            var searchDir  = Directory.Exists(currentDir) ? currentDir : dir;

            var exes = Directory.GetFiles(searchDir, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(e => !Path.GetFileName(e).StartsWith("Update", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (exes.Length == 0) return;

            // Prefer exe whose name matches folder name
            var best = exes.FirstOrDefault(e =>
                Path.GetFileNameWithoutExtension(e).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                ?? exes.OrderByDescending(e => new FileInfo(e).Length).First();

            var exeName     = Path.GetFileNameWithoutExtension(best).ToLower();
            var displayName = ToTitleCase(folderName);

            if (!apps.ContainsKey(displayName))
                apps[displayName] = new InstalledApp
                {
                    DisplayName    = displayName,
                    ExecutableName = exeName,
                    InstallLocation = dir
                };
            else if (string.IsNullOrEmpty(apps[displayName].ExecutableName))
                apps[displayName].ExecutableName = exeName;
        }
        catch { }
    }

    // Always merge running processes so any open app is always findable
    private static void MergeRunningProcesses(Dictionary<string, InstalledApp> apps)
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var procName = p.ProcessName;
                    if (string.IsNullOrEmpty(procName)) continue;

                    // If we already have it by exe name, skip
                    if (apps.Values.Any(a =>
                            a.ExecutableName.Equals(procName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var displayName = ToTitleCase(procName);
                    if (!apps.ContainsKey(displayName))
                        apps[displayName] = new InstalledApp
                        {
                            DisplayName    = displayName,
                            ExecutableName = procName.ToLower()
                        };
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? TryResolveExecutable(string? installLocation, string? displayIcon,
        string displayName)
    {
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim('"', ' ');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(iconPath).ToLower();
        }

        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                var exes = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
                var mainExe = exes
                    .Select(e => new FileInfo(e))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();
                if (mainExe != null)
                    return Path.GetFileNameWithoutExtension(mainExe.Name).ToLower();
            }
            catch { }
        }

        return SanitizeToProcessName(displayName);
    }

    private static string SanitizeToProcessName(string displayName)
    {
        var clean = displayName.Split(' ')[0].ToLower();
        return new string(clean.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
    }

    private static string ToTitleCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Split on camelCase and spaces
        return System.Text.RegularExpressions.Regex
            .Replace(s, @"(?<=[a-z])(?=[A-Z])", " ")
            .Replace("-", " ").Replace("_", " ")
            .Trim();
    }

    private static void AddWellKnownApps(Dictionary<string, InstalledApp> apps)
    {
        // Name → process name. These fill in missing entries OR correct exe names.
        var wellKnown = new[]
        {
            ("Claude",                    "claude"),
            ("Visual Studio Code",        "code"),
            ("Visual Studio 2022",        "devenv"),
            ("Visual Studio 2019",        "devenv"),
            ("Google Chrome",             "chrome"),
            ("Mozilla Firefox",           "firefox"),
            ("Microsoft Edge",            "msedge"),
            ("Spotify",                   "spotify"),
            ("Discord",                   "discord"),
            ("Slack",                     "slack"),
            ("Figma",                     "figma"),
            ("Notion",                    "notion"),
            ("Telegram Desktop",          "telegram"),
            ("WhatsApp",                  "whatsapp"),
            ("Zoom",                      "zoom"),
            ("Microsoft Teams",           "ms-teams"),
            ("Obsidian",                  "obsidian"),
            ("Notepad++",                 "notepad++"),
            ("Adobe Photoshop 2024",      "photoshop"),
            ("Adobe Illustrator 2024",    "illustrator"),
            ("OBS Studio",                "obs64"),
            ("VLC media player",          "vlc"),
            ("Windows Terminal",          "windowsterminal"),
            ("PowerShell",                "powershell"),
            ("File Explorer",             "explorer"),
            ("Notepad",                   "notepad"),
            ("Microsoft Word",            "winword"),
            ("Microsoft Excel",           "excel"),
            ("Microsoft PowerPoint",      "powerpnt"),
            ("Microsoft Outlook",         "outlook"),
            ("IntelliJ IDEA",             "idea64"),
            ("PyCharm",                   "pycharm64"),
            ("Rider",                     "rider64"),
            ("WebStorm",                  "webstorm64"),
            ("Android Studio",            "studio64"),
            ("Postman",                   "postman"),
            ("Docker Desktop",            "com.docker.backend"),
            ("Blender",                   "blender"),
            ("Unity",                     "unity"),
            ("Unreal Engine",             "unrealEditor"),
            ("Steam",                     "steam"),
            ("Epic Games Launcher",       "epicgameslauncher"),
        };

        foreach (var (name, proc) in wellKnown)
        {
            if (!apps.ContainsKey(name))
                apps[name] = new InstalledApp { DisplayName = name, ExecutableName = proc };
            else if (string.IsNullOrEmpty(apps[name].ExecutableName))
                apps[name].ExecutableName = proc;
        }
    }

    public static List<InstalledApp> GetRunningApps()
    {
        return Process.GetProcesses()
            .Where(p =>
            {
                try { return p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.ProcessName); }
                catch { return false; }
            })
            .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(p => new InstalledApp
            {
                DisplayName    = ToTitleCase(p.ProcessName),
                ExecutableName = p.ProcessName.ToLower()
            })
            .OrderBy(a => a.DisplayName)
            .ToList();
    }
}

public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
