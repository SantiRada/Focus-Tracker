using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace FocusTracker.Plugins;

/// <summary>
/// High-level coordinator for the plugin lifecycle.
///
/// Responsibilities
/// ────────────────
/// • Scan the plugins folder at startup and load every enabled .focusplugin.
/// • Expose Install / Uninstall / Enable / Disable operations for a future
///   Plugins UI panel.
/// • Keep a reference to each plugin's <see cref="PluginLoadContext"/> so the
///   assembly can be unloaded when the plugin is disabled or uninstalled.
/// • Persist the disabled-plugin list across restarts.
///
/// Security model
/// ──────────────
/// This class is the only entry point for loading foreign code into the process.
/// Every plugin goes through <see cref="PluginLoader.Load"/> which:
///   – validates the manifest before touching any DLL;
///   – extracts into an isolated temp folder;
///   – loads the DLL in a collectible <see cref="PluginLoadContext"/> that blocks
///     direct access to the host's DB and WPF layer;
///   – verifies Id consistency between the manifest and the class.
/// After successful loading, the plugin receives only an <see cref="IPluginContext"/>
/// facade — it has no reference to DatabaseService, TrackingService, or any view.
/// </summary>
public sealed class PluginManager
{
    // ── Singleton ──────────────────────────────────────────────────────────
    private static PluginManager? _instance;
    public static PluginManager Instance => _instance ??= new PluginManager();

    // ── Internal state ─────────────────────────────────────────────────────
    private readonly Dictionary<string, PluginLoadContext>  _contexts     = new();
    private readonly Dictionary<string, PluginManifest>     _manifests    = new();
    private readonly Dictionary<string, PluginHelpContent>  _helpContents = new();
    private          HashSet<string>                        _disabled     = new(StringComparer.OrdinalIgnoreCase);
    private readonly object                                 _lock         = new();

    // ── Paths ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Root folder where .focusplugin files are stored.
    /// Defaults to %APPDATA%\FocusTracker\plugins.
    /// </summary>
    public string PluginsFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FocusTracker", "plugins");

    private string DisabledListPath =>
        Path.Combine(PluginsFolder, "_disabled.json");

    // ── Startup ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once by App.xaml.cs immediately after
    /// <see cref="PluginRegistry.SetHost"/> has been called.
    /// Scans <see cref="PluginsFolder"/> and loads every enabled .focusplugin.
    /// Errors per-plugin are logged to crash.log and never propagate to the host.
    /// </summary>
    public void LoadAll()
    {
        Directory.CreateDirectory(PluginsFolder);
        LoadDisabledList();

        foreach (var file in Directory.EnumerateFiles(PluginsFolder, "*.focusplugin"))
        {
            var error = TryLoadOne(file);
            if (error != null)
                LogError($"Skipped '{Path.GetFileName(file)}': {error}");
        }
    }

    // ── Install ────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies a .focusplugin file from any location into the plugins folder,
    /// then loads it immediately.
    /// </summary>
    /// <returns>
    /// <c>null</c> on success, or a human-readable error message.
    /// </returns>
    public string? Install(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
            return "File not found.";

        var destPath = Path.Combine(PluginsFolder, Path.GetFileName(sourceFilePath));

        // If the same file is already there, re-install (update) it.
        try
        {
            File.Copy(sourceFilePath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            return $"Could not copy plugin file: {ex.Message}";
        }

        return TryLoadOne(destPath);
    }

    // ── Uninstall ──────────────────────────────────────────────────────────

    /// <summary>
    /// Shuts down a plugin, unloads its assembly, and deletes its .focusplugin file.
    /// </summary>
    /// <returns>
    /// <c>null</c> on success, or a human-readable error message.
    /// </returns>
    public string? Uninstall(string pluginId)
    {
        ShutdownAndUnload(pluginId);

        var file = FindPluginFile(pluginId);
        if (file != null)
        {
            try   { File.Delete(file); }
            catch (Exception ex) { return $"Could not delete plugin file: {ex.Message}"; }
        }

        lock (_lock)
        {
            _manifests.Remove(pluginId);
            _contexts.Remove(pluginId);
            _disabled.Remove(pluginId);
        }

        SaveDisabledList();
        return null;
    }

    // ── Enable / Disable ───────────────────────────────────────────────────

    /// <summary>
    /// Marks a plugin as enabled and loads it if it isn't already running.
    /// </summary>
    /// <returns>
    /// <c>null</c> on success, or a human-readable error message.
    /// </returns>
    public string? Enable(string pluginId)
    {
        lock (_lock) { _disabled.Remove(pluginId); }
        SaveDisabledList();

        // If the plugin is not running yet, try to load it now.
        if (PluginRegistry.Instance.GetPlugin(pluginId) == null)
        {
            var file = FindPluginFile(pluginId);
            if (file == null) return $"Plugin file for '{pluginId}' not found in plugins folder.";
            return TryLoadOne(file);
        }

        return null;
    }

    /// <summary>
    /// Shuts down a plugin and marks it as disabled so it is not loaded on next startup.
    /// </summary>
    public void Disable(string pluginId)
    {
        lock (_lock) { _disabled.Add(pluginId); }
        SaveDisabledList();
        ShutdownAndUnload(pluginId);
    }

    // ── Introspection ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns manifests for all discovered plugins (loaded, disabled, or errored).
    /// Useful for building a "Plugins" settings panel.
    /// </summary>
    public IReadOnlyList<PluginManifest> GetAllManifests()
    {
        lock (_lock) { return _manifests.Values.ToList().AsReadOnly(); }
    }

    /// <summary>Returns <c>true</c> if the plugin is in the disabled list.</summary>
    public bool IsDisabled(string pluginId)
    {
        lock (_lock) { return _disabled.Contains(pluginId); }
    }

    /// <summary>
    /// Returns the cached <see cref="PluginHelpContent"/> for the given plugin, or
    /// <c>null</c> if the plugin has never been loaded in this session.
    /// The content is available even after the plugin is disabled or unloaded.
    /// </summary>
    public PluginHelpContent? GetHelpContent(string pluginId)
    {
        lock (_lock)
        {
            return _helpContents.TryGetValue(pluginId, out var h) ? h : null;
        }
    }

    // ── Core loading helper ────────────────────────────────────────────────

    /// <summary>
    /// Loads a single .focusplugin file through <see cref="PluginLoader"/>,
    /// registers it with <see cref="PluginRegistry"/>, and stores its context.
    /// </summary>
    /// <returns>null on success, or an error message.</returns>
    private string? TryLoadOne(string filePath)
    {
        try
        {
            var (plugin, context, manifest) = PluginLoader.Load(filePath);

            lock (_lock)
            {
                // Always store the manifest and cache help content so "Mis plugins"
                // can list every installed plugin, including disabled ones.
                _manifests[plugin.Id] = manifest;
                try
                {
                    var help = plugin.GetUserHelp();
                    if (help != null)
                        _helpContents[plugin.Id] = help;
                }
                catch { /* validated by PluginLoader; swallow here */ }

                // Disabled? Unload after caching metadata — don't run Initialize.
                if (_disabled.Contains(plugin.Id))
                {
                    context.Unload();
                    return null; // expected: skip init, plugin listed but inactive
                }

                // Already loaded? (e.g. two .focusplugin files with the same id)
                if (_contexts.ContainsKey(plugin.Id))
                {
                    context.Unload();
                    return $"Plugin '{plugin.Id}' is already loaded. " +
                           "Remove the duplicate .focusplugin file.";
                }

                _contexts[plugin.Id] = context;
            }

            // Register calls IFocusPlugin.Initialize — this is when plugin code first runs.
            PluginRegistry.Instance.Register(plugin);
            return null;
        }
        catch (PluginLoadException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return $"Unexpected error: {ex.Message}";
        }
    }

    // ── Lifecycle helpers ──────────────────────────────────────────────────

    private void ShutdownAndUnload(string pluginId)
    {
        var plugin = PluginRegistry.Instance.GetPlugin(pluginId);
        if (plugin != null)
        {
            try   { plugin.Shutdown(); }
            catch (Exception ex)
            { LogError($"Plugin '{pluginId}' threw during Shutdown: {ex.Message}"); }
        }

        PluginLoadContext? ctx;
        lock (_lock)
        {
            _contexts.TryGetValue(pluginId, out ctx);
            _contexts.Remove(pluginId); // must clear so TryLoadOne can re-register on Enable
        }
        ctx?.Unload();
    }

    // ── File helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Finds the .focusplugin file whose manifest declares the given id.
    /// Returns null if no such file exists.
    /// </summary>
    private string? FindPluginFile(string pluginId)
    {
        foreach (var file in Directory.EnumerateFiles(PluginsFolder, "*.focusplugin"))
        {
            try
            {
                using var archive = ZipFile.OpenRead(file);
                var entry = archive.GetEntry("plugin.json");
                if (entry == null) continue;

                using var reader = new StreamReader(entry.Open());
                var manifest = PluginManifest.TryLoad(reader.ReadToEnd());
                if (string.Equals(manifest?.Id, pluginId, StringComparison.Ordinal))
                    return file;
            }
            catch { /* corrupted file — skip */ }
        }
        return null;
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private void LoadDisabledList()
    {
        try
        {
            if (!File.Exists(DisabledListPath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(
                           File.ReadAllText(DisabledListPath));
            lock (_lock)
            {
                _disabled = new HashSet<string>(
                    list ?? [],
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* corrupt file — start fresh */ }
    }

    private void SaveDisabledList()
    {
        try
        {
            HashSet<string> snapshot;
            lock (_lock) { snapshot = new HashSet<string>(_disabled); }
            File.WriteAllText(
                DisabledListPath,
                JsonSerializer.Serialize(snapshot.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Logging ────────────────────────────────────────────────────────────

    private static void LogError(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FocusTracker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PluginManager] {msg}\n");
        }
        catch { }
    }
}
