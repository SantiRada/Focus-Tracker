using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace FocusTracker.Plugins;

/// <summary>
/// An isolated <see cref="AssemblyLoadContext"/> for a single plugin.
///
/// Why isolation matters
/// ─────────────────────
/// If the host and a plugin each independently load a copy of FocusTracker.PluginSDK,
/// the CLR treats the two copies as distinct types even if they are byte-for-byte
/// identical — so casting a plugin instance to <see cref="IFocusPlugin"/> would silently
/// return null.
///
/// The fix: this context is marked <c>isCollectible: true</c> (allows unloading) and
/// intentionally returns <c>null</c> for SDK types so the CLR resolves them from the
/// host's already-loaded copy. All other dependency assemblies are loaded from the
/// plugin's own extraction folder, keeping plugins fully isolated from each other.
///
/// Security boundary
/// ─────────────────
/// Plugins interact with host data exclusively through <see cref="IPluginContext"/>.
/// They physically cannot obtain references to <see cref="Data.DatabaseService"/> or
/// <see cref="Services.TrackingService"/> because those types are never exported via the SDK,
/// and this context blocks any attempt to load the host assembly directly.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string                      _pluginFolder;
    private readonly AssemblyDependencyResolver  _resolver;

    /// <summary>
    /// Assembly names that MUST be resolved from the host's default ALC.
    /// Returning null for these causes the CLR to fall back to the host,
    /// guaranteeing type identity for all SDK interfaces and models.
    /// </summary>
    private static readonly HashSet<string> _hostOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        "FocusTracker.PluginSDK",   // IFocusPlugin, IPluginContext, models
        "System.Runtime",
        "System.Runtime.Loader",
        "netstandard",
    };

    /// <summary>
    /// Assembly names that plugins must NOT load — they would give direct access to
    /// host internals (DB connection, WPF layer, etc.).
    /// </summary>
    private static readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "FocusTracker",             // host executable — never hand this to a plugin
        "Microsoft.Data.Sqlite",    // raw DB access — plugins go through IPluginContext
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.batteries_v2",
        "SQLitePCLRaw.provider.dynamic_cdecl",
    };

    /// <param name="pluginFolder">
    /// Directory where the plugin archive was extracted.
    /// </param>
    /// <param name="mainAssemblyPath">
    /// Full path to the plugin's entry DLL. Used to build a
    /// <see cref="AssemblyDependencyResolver"/> that reads the plugin's .deps.json.
    /// </param>
    public PluginLoadContext(string pluginFolder, string mainAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        _pluginFolder = pluginFolder;
        _resolver     = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    // ── Assembly resolution ───────────────────────────────────────────────

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? string.Empty;

        // 1. Blocked: actively deny access to host internals.
        //    Throwing here prevents the CLR from falling back to the Default ALC,
        //    which would otherwise resolve the already-loaded host assembly and hand
        //    it to the plugin — bypassing the entire API sandbox.
        if (_blocked.Contains(name))
            throw new FileNotFoundException(
                $"Assembly '{name}' is not accessible to plugins. " +
                $"Use IPluginContext to interact with FocusTracker data.");


        // 2. Host-owned: return null so the CLR uses the host's already-loaded copy.
        //    This is the key step that guarantees IFocusPlugin type identity.
        if (_hostOwned.Contains(name))
            return null;

        // 3. Use the plugin's own dependency graph (.deps.json) if available.
        //    Load via stream to avoid locking the file on disk (Windows file lock
        //    would prevent overwriting on reinstall / update).
        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolvedPath != null)
            return LoadFromStream(new System.IO.MemoryStream(File.ReadAllBytes(resolvedPath)));

        // 4. Scan the plugin extraction folder by filename.
        var candidate = Path.Combine(_pluginFolder, name + ".dll");
        if (File.Exists(candidate))
            return LoadFromStream(new System.IO.MemoryStream(File.ReadAllBytes(candidate)));

        // 5. Fall through to the host's default ALC for remaining BCL types.
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var resolvedPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return resolvedPath != null
            ? LoadUnmanagedDllFromPath(resolvedPath)
            : IntPtr.Zero;
    }
}
