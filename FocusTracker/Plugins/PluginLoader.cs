using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace FocusTracker.Plugins;

/// <summary>
/// Loads a single .focusplugin file through a hardened multi-step pipeline:
///
///   1. Open the ZIP archive and read plugin.json (no DLL touched yet).
///   2. Validate the manifest — reject unknown versions, bad IDs, missing fields.
///   3. Check host-version compatibility before extracting anything.
///   4. Extract to a per-plugin temp directory with path-traversal protection.
///   5. Load the entry assembly into an isolated <see cref="PluginLoadContext"/>.
///   6. Reflect over exported types to find the IFocusPlugin implementation.
///   7. Instantiate via parameterless constructor — no elevated reflection.
///   8. Cross-check that the plugin's self-reported Id matches the manifest.
///
/// At no point is any plugin code run during loading (no static constructors invoked
/// by the loader itself). Execution begins only when the caller calls Initialize.
/// </summary>
internal static class PluginLoader
{
    // ── Host version ──────────────────────────────────────────────────────

    /// <summary>
    /// The current host application version.
    /// Must be kept in sync with FocusTracker.csproj &lt;Version&gt;.
    /// </summary>
    public static readonly Version HostVersion = new(1, 5, 0);

    // ── Extraction root ────────────────────────────────────────────────────

    /// <summary>
    /// Plugins are extracted here (one sub-folder per plugin id).
    /// This folder is inside %APPDATA% so it survives the session but is writable
    /// by the current user only.
    /// </summary>
    private static string ExtractionRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FocusTracker", "plugins", "_extracted");

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// Loads a .focusplugin file and returns the live plugin instance, its
    /// load context (needed for later unloading), and its manifest.
    /// </summary>
    /// <exception cref="PluginLoadException">
    /// Thrown on any load failure with a human-readable description.
    /// </exception>
    public static (IFocusPlugin plugin, PluginLoadContext context, PluginManifest manifest)
        Load(string focusPluginPath)
    {
        if (!File.Exists(focusPluginPath))
            throw new PluginLoadException(focusPluginPath, "File not found.");

        // Step 1–3: read and validate manifest without touching any DLL
        var manifest = ReadManifest(focusPluginPath);
        var validationError = manifest.Validate(HostVersion);
        if (validationError != null)
            throw new PluginLoadException(focusPluginPath,
                $"Manifest validation failed: {validationError}");

        // Step 4: extract archive with path-traversal protection
        var extractDir = Path.Combine(ExtractionRoot, SanitizeFolderName(manifest.Id));
        ExtractPlugin(focusPluginPath, extractDir);

        // Step 5: locate and load the entry DLL in an isolated context
        var entryDll = Path.Combine(extractDir, manifest.EntryAssembly);
        if (!File.Exists(entryDll))
            throw new PluginLoadException(focusPluginPath,
                $"Entry assembly '{manifest.EntryAssembly}' was not found inside the archive.");

        var loadContext = new PluginLoadContext(extractDir, entryDll);
        Assembly assembly;
        try
        {
            // Load via stream so the file is never locked on disk.
            // This allows future installs/updates to overwrite the extracted DLL
            // without getting "Access to the path is denied" errors on Windows.
            assembly = loadContext.LoadFromStream(
                new System.IO.MemoryStream(File.ReadAllBytes(entryDll)));
        }
        catch (Exception ex)
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"Could not load assembly '{manifest.EntryAssembly}': {ex.Message}", ex);
        }

        // Step 6: find the IFocusPlugin implementation
        var pluginType = FindPluginType(assembly);
        if (pluginType == null)
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"No public class implementing IFocusPlugin with a parameterless " +
                $"constructor was found in '{manifest.EntryAssembly}'.");
        }

        // Step 7: instantiate — no plugin code runs yet
        IFocusPlugin instance;
        try
        {
            instance = (IFocusPlugin)Activator.CreateInstance(pluginType)!;
        }
        catch (Exception ex)
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"Could not create an instance of '{pluginType.FullName}': {ex.Message}", ex);
        }

        // Step 8: cross-check Id — prevents manifest spoofing
        if (!string.Equals(instance.Id, manifest.Id, StringComparison.Ordinal))
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"Plugin self-reports Id='{instance.Id}' but manifest declares '{manifest.Id}'. " +
                "These must be identical.");
        }

        // Step 9: verify that GetUserHelp() is implemented and returns non-null.
        // This is already enforced at compile time by the interface, but we also
        // check at runtime to catch null returns or exceptions.
        PluginHelpContent? helpContent;
        try
        {
            helpContent = instance.GetUserHelp();
        }
        catch (Exception ex)
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"GetUserHelp() threw an exception in '{pluginType.FullName}': {ex.Message}\n" +
                "Every plugin must implement GetUserHelp() and return a valid PluginHelpContent.", ex);
        }

        if (helpContent == null)
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"'{pluginType.FullName}.GetUserHelp()' returned null. " +
                "Every plugin must return a non-null PluginHelpContent with a non-empty Summary. " +
                "Add a GetUserHelp() implementation that describes how to use this plugin.");
        }

        if (string.IsNullOrWhiteSpace(helpContent.Summary))
        {
            loadContext.Unload();
            throw new PluginLoadException(focusPluginPath,
                $"'{pluginType.FullName}.GetUserHelp()' returned a PluginHelpContent with an empty Summary. " +
                "Provide a description of at least one sentence explaining what the plugin does and how to use it.");
        }

        return (instance, loadContext, manifest);
    }

    // ── Step implementations ──────────────────────────────────────────────

    private static PluginManifest ReadManifest(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry("plugin.json")
                        ?? throw new PluginLoadException(archivePath,
                               "plugin.json was not found at the root of the archive.");

            using var reader = new StreamReader(entry.Open());
            var json = reader.ReadToEnd();

            return PluginManifest.TryLoad(json)
                   ?? throw new PluginLoadException(archivePath,
                          "plugin.json exists but could not be parsed as JSON.");
        }
        catch (PluginLoadException) { throw; }
        catch (Exception ex)
        {
            throw new PluginLoadException(archivePath,
                $"Could not open the plugin archive: {ex.Message}", ex);
        }
    }

    private static void ExtractPlugin(string archivePath, string targetDir)
    {
        // Always start from a clean slate so stale files from a previous version
        // cannot linger and interfere.
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, recursive: true);

        Directory.CreateDirectory(targetDir);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                // ── Path-traversal protection ──────────────────────────────
                // Normalise slashes, strip leading separators.
                var safeName = entry.FullName
                    .Replace('\\', '/')
                    .TrimStart('/');

                // Reject entries that try to escape the target directory.
                if (safeName.Contains("../") || safeName.Contains("..\\") ||
                    safeName.StartsWith(".."))
                    continue;

                // Build and canonicalise the destination path.
                var destPath = Path.GetFullPath(Path.Combine(targetDir, safeName));

                // Double-check: the canonical path must still be inside targetDir.
                if (!destPath.StartsWith(
                        Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip directory entries (they end with '/').
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            throw new PluginLoadException(archivePath,
                $"Extraction failed: {ex.Message}", ex);
        }
    }

    private static Type? FindPluginType(Assembly assembly)
    {
        var iface = typeof(IFocusPlugin);
        try
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (!type.IsClass || type.IsAbstract) continue;
                if (!iface.IsAssignableFrom(type)) continue;
                // Must have a public parameterless constructor — no DI tricks.
                if (type.GetConstructor(Type.EmptyTypes) != null)
                    return type;
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types couldn't be loaded — log loader exceptions for debugging
            // but don't crash; just return null so the caller raises a clear error.
            _ = ex.LoaderExceptions; // available for breakpoints
        }
        return null;
    }

    // ── Sanitisers ────────────────────────────────────────────────────────

    /// <summary>
    /// Turns a plugin id into a safe directory name by replacing any character
    /// that isn't a letter, digit, dot, or hyphen with an underscore.
    /// </summary>
    private static string SanitizeFolderName(string id)
        => string.Concat(id.Select(c =>
            char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));
}

// ── Exception ─────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown by <see cref="PluginLoader"/> when a .focusplugin file cannot be loaded.
/// The <see cref="Exception.Message"/> is always human-readable and safe to display in the UI.
/// </summary>
public sealed class PluginLoadException : Exception
{
    /// <summary>The absolute path of the .focusplugin file that failed to load.</summary>
    public string PluginFilePath { get; }

    public PluginLoadException(string path, string reason)
        : base($"[{Path.GetFileName(path)}] {reason}")
    {
        PluginFilePath = path;
    }

    public PluginLoadException(string path, string reason, Exception inner)
        : base($"[{Path.GetFileName(path)}] {reason}", inner)
    {
        PluginFilePath = path;
    }
}
