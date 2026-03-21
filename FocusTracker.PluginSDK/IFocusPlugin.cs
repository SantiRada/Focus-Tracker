// ── FocusTracker.PluginSDK — IFocusPlugin.cs ─────────────────────────────────
namespace FocusTracker.Plugins;

/// <summary>
/// Contract every FocusTracker plugin must implement.
/// Discovered at host startup, initialised with an <see cref="IPluginContext"/>,
/// and shut down when the host exits.
/// </summary>
public interface IFocusPlugin
{
    // ── Identity ────────────────────────────────────────────────────────────

    /// <summary>
    /// Unique reverse-DNS identifier. Must not change across versions.
    /// Example: "com.yourname.myplugin"
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable display name shown in the plugin store.</summary>
    string Name { get; }

    /// <summary>Semantic version string. Example: "1.0.0"</summary>
    string Version { get; }

    /// <summary>Author or organisation name.</summary>
    string Author { get; }

    /// <summary>Short description (≤ 140 chars) of what the plugin does.</summary>
    string Description { get; }

    // ── Documentation (REQUIRED) ────────────────────────────────────────────

    /// <summary>
    /// Returns the user-facing help content for this plugin.
    ///
    /// ⚠️  This method is REQUIRED.  Any plugin class that does not implement it
    /// will fail to compile with the error:
    ///   "does not implement interface member 'IFocusPlugin.GetUserHelp()'"
    ///
    /// The returned <see cref="PluginHelpContent"/> must not be null.
    /// This method is called by the host at load time (before
    /// <see cref="Initialize"/> runs) so it must not depend on any
    /// initialised state — keep it pure and stateless.
    ///
    /// The content is displayed in Plugin Manager → Mis plugins → Ayuda.
    /// </summary>
    PluginHelpContent GetUserHelp();

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called once after the host finishes its startup sequence.
    /// Subscribe to events, register settings, and store the context here.
    /// </summary>
    void Initialize(IPluginContext context);

    /// <summary>
    /// Called when the host is about to exit.
    /// Release resources, flush data, unsubscribe from events.
    /// </summary>
    void Shutdown();
}
