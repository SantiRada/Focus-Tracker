using System.IO;
using FocusTracker.Data;
using FocusTracker.Services;

namespace FocusTracker.Plugins;

/// <summary>
/// Central hub that owns all registered plugins and their setting contributions.
/// Accessed via the singleton <see cref="Instance"/> property.
/// </summary>
public sealed class PluginRegistry
{
    // ── Singleton ─────────────────────────────────────────────────────────
    private static PluginRegistry? _instance;

    /// <summary>The application-wide singleton instance of the registry.</summary>
    public static PluginRegistry Instance => _instance ??= new PluginRegistry();

    // ── Internal state ────────────────────────────────────────────────────
    private readonly List<IFocusPlugin>                                           _plugins  = new();
    private readonly List<(string PluginId, PluginSettingDescriptor Descriptor)>  _settings = new();
    private readonly List<PluginButtonContribution>                               _buttons  = new();
    private readonly List<PluginCardContribution>                                 _homeCards = new();
    private readonly object _lock = new();

    private DatabaseService?  _db;
    private TrackingService?  _tracker;
    private bool _hostReady = false;

    // ── Session lifecycle events (fired by the host, consumed by plugins) ──
    private event Action<int?>? _trackingStarted;
    private event Action?       _trackingStopped;

    /// <summary>Called by the host immediately after a tracking session starts.</summary>
    internal void NotifyTrackingStarted(int? projectId)
    {
        Action<int?>? snap;
        lock (_lock) { snap = _trackingStarted; }
        snap?.Invoke(projectId);
    }

    /// <summary>Called by the host immediately after a tracking session stops.</summary>
    internal void NotifyTrackingStopped()
    {
        Action? snap;
        lock (_lock) { snap = _trackingStopped; }
        snap?.Invoke();
    }

    internal void AddTrackingStartedHandler(Action<int?> h)    { lock (_lock) _trackingStarted += h; }
    internal void RemoveTrackingStartedHandler(Action<int?> h) { lock (_lock) _trackingStarted -= h; }
    internal void AddTrackingStoppedHandler(Action h)          { lock (_lock) _trackingStopped += h; }
    internal void RemoveTrackingStoppedHandler(Action h)       { lock (_lock) _trackingStopped -= h; }

    // ── Host initialisation (called once by App.xaml.cs) ─────────────────

    /// <summary>
    /// Must be called by the host application after its own services are ready.
    /// Stores references to DB and tracker so that <see cref="PluginContext"/>
    /// instances can be constructed.
    /// </summary>
    internal void SetHost(DatabaseService db, TrackingService tracker)
    {
        lock (_lock)
        {
            _db      = db;
            _tracker = tracker;
            _hostReady = true;
        }
    }

    // ── Plugin registration ───────────────────────────────────────────────

    /// <summary>
    /// Registers a plugin, constructs its <see cref="PluginContext"/>, and calls
    /// <see cref="IFocusPlugin.Initialize"/>.
    /// Duplicate plugin IDs are silently ignored.
    /// Any exception thrown by the plugin's <c>Initialize</c> is caught and logged
    /// so that a faulty plugin cannot crash the host.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="SetHost"/> has not been called yet.
    /// </exception>
    public void Register(IFocusPlugin plugin)
    {
        lock (_lock)
        {
            if (!_hostReady)
                throw new InvalidOperationException(
                    "PluginRegistry.SetHost must be called before registering plugins.");

            if (_plugins.Any(p => p.Id == plugin.Id)) return;

            _plugins.Add(plugin);
        }

        var ctx = new PluginContext(_db!, _tracker!, this, plugin.Id);
        try { plugin.Initialize(ctx); }
        catch (Exception ex)
        {
            LogError(plugin.Id, "Initialize", ex);
        }
    }

    // ── Unregister (runtime uninstall / disable) ──────────────────────────

    /// <summary>
    /// Removes all contributions (settings, buttons, cards) registered by the given plugin.
    /// Must be called before the plugin's assembly is unloaded.
    /// </summary>
    internal void UnregisterContributions(string pluginId)
    {
        lock (_lock)
        {
            _plugins.RemoveAll(p => p.Id == pluginId);
            _settings.RemoveAll(s => s.PluginId == pluginId);
            // Buttons and cards are keyed by pluginId via the contribution owner dict
            if (_buttonOwners.TryGetValue(pluginId, out var btnSet))
            {
                _buttons.RemoveAll(b => btnSet.Contains(b));
                _buttonOwners.Remove(pluginId);
            }
            if (_cardOwners.TryGetValue(pluginId, out var cardSet))
            {
                _homeCards.RemoveAll(c => cardSet.Contains(c));
                _cardOwners.Remove(pluginId);
            }
        }
    }

    // Ownership dictionaries so we can clean up on uninstall
    private readonly Dictionary<string, HashSet<PluginButtonContribution>>  _buttonOwners = new();
    private readonly Dictionary<string, HashSet<PluginCardContribution>>    _cardOwners   = new();

    // ── Shutdown ──────────────────────────────────────────────────────────

    /// <summary>
    /// Calls <see cref="IFocusPlugin.Shutdown"/> on every registered plugin in reverse
    /// registration order. Exceptions are caught per-plugin so all receive a shutdown call.
    /// </summary>
    internal void ShutdownAll()
    {
        List<IFocusPlugin> snapshot;
        lock (_lock) { snapshot = _plugins.AsEnumerable().Reverse().ToList(); }

        foreach (var plugin in snapshot)
        {
            try { plugin.Shutdown(); }
            catch (Exception ex) { LogError(plugin.Id, "Shutdown", ex); }
        }
    }

    // ── Static cross-layer events (decouples PluginContext from MainWindow) ──

    /// <summary>
    /// Raised on the UI thread to ask MainWindow to rebuild the plugin settings panel.
    /// MainWindow subscribes in its constructor.
    /// </summary>
    internal static event Action? SettingsRefreshRequested;

    /// <summary>Raises <see cref="SettingsRefreshRequested"/> to trigger a UI rebuild.</summary>
    internal static void RequestSettingsRefresh() => SettingsRefreshRequested?.Invoke();

    /// <summary>
    /// Raised when a plugin wants to navigate the host to its settings tab.
    /// Parameter is the settings tab name (e.g. "Límite de uso").
    /// </summary>
    internal static event Action<string>? NavigateToPluginSettingsRequested;

    /// <summary>Raises <see cref="NavigateToPluginSettingsRequested"/>.</summary>
    internal static void RequestNavigateToPluginSettings(string tabName)
        => NavigateToPluginSettingsRequested?.Invoke(tabName);

    // ── Setting contributions ─────────────────────────────────────────────

    /// <summary>
    /// Registers a setting descriptor contributed by a plugin.
    /// Called internally by <see cref="PluginContext.RegisterSetting"/>.
    /// </summary>
    internal void RegisterSetting(string pluginId, PluginSettingDescriptor descriptor)
    {
        lock (_lock)
        {
            _settings.Add((pluginId, descriptor));
        }
    }

    /// <summary>
    /// Removes all setting descriptors previously registered by the given plugin.
    /// Called by <see cref="PluginContext.RefreshSettings"/> before re-registering.
    /// </summary>
    internal void ClearSettingsForPlugin(string pluginId)
    {
        lock (_lock)
        {
            _settings.RemoveAll(s => s.PluginId == pluginId);
        }
    }

    /// <summary>
    /// Returns a read-only snapshot of all plugin-contributed setting descriptors,
    /// grouped by <see cref="PluginSettingDescriptor.TabName"/>.
    /// </summary>
    public IReadOnlyList<(string PluginId, PluginSettingDescriptor Descriptor)> GetAllSettings()
    {
        lock (_lock) { return _settings.ToList().AsReadOnly(); }
    }

    /// <summary>
    /// Returns all distinct tab names contributed by plugins, in registration order.
    /// </summary>
    public IReadOnlyList<string> GetSettingTabNames()
    {
        lock (_lock)
        {
            return _settings
                .Select(s => s.Descriptor.TabName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Returns all setting descriptors for a specific tab name.
    /// </summary>
    public IReadOnlyList<PluginSettingDescriptor> GetSettingsForTab(string tabName)
    {
        lock (_lock)
        {
            return _settings
                .Where(s => s.Descriptor.TabName.Equals(tabName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Descriptor)
                .ToList()
                .AsReadOnly();
        }
    }

    // ── Button contributions ──────────────────────────────────────────────

    internal void RegisterButton(string pluginId, PluginButtonContribution btn)
    {
        lock (_lock)
        {
            _buttons.Add(btn);
            if (!_buttonOwners.ContainsKey(pluginId))
                _buttonOwners[pluginId] = new HashSet<PluginButtonContribution>(ReferenceEqualityComparer.Instance);
            _buttonOwners[pluginId].Add(btn);
        }
    }

    /// <summary>
    /// Returns all button contributions for the specified screen, in registration order.
    /// </summary>
    public IReadOnlyList<PluginButtonContribution> GetButtonsForScreen(PluginScreenTarget screen)
    {
        lock (_lock)
        {
            return _buttons
                .Where(b => b.Screen == screen)
                .ToList()
                .AsReadOnly();
        }
    }

    // ── Home card contributions ────────────────────────────────────────────

    internal void RegisterHomeCard(string pluginId, PluginCardContribution card)
    {
        lock (_lock)
        {
            _homeCards.Add(card);
            if (!_cardOwners.ContainsKey(pluginId))
                _cardOwners[pluginId] = new HashSet<PluginCardContribution>(ReferenceEqualityComparer.Instance);
            _cardOwners[pluginId].Add(card);
        }
    }

    /// <summary>Returns all home-screen card contributions in registration order.</summary>
    public IReadOnlyList<PluginCardContribution> GetHomeCards()
    {
        lock (_lock) { return _homeCards.ToList().AsReadOnly(); }
    }

    // ── Introspection ─────────────────────────────────────────────────────

    /// <summary>Returns a read-only list of all currently registered plugins.</summary>
    public IReadOnlyList<IFocusPlugin> GetAllPlugins()
    {
        lock (_lock) { return _plugins.ToList().AsReadOnly(); }
    }

    /// <summary>
    /// Returns the plugin with the given <paramref name="id"/>, or <c>null</c> if not found.
    /// </summary>
    public IFocusPlugin? GetPlugin(string id)
    {
        lock (_lock) { return _plugins.FirstOrDefault(p => p.Id == id); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static void LogError(string pluginId, string phase, Exception ex)
    {
        try
        {
            var dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FocusTracker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Plugin '{pluginId}' threw during {phase}:\n{ex}\n");
        }
        catch { }
    }
}
