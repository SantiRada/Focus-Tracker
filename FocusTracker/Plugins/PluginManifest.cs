using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusTracker.Plugins;

/// <summary>
/// Represents the plugin.json manifest bundled inside every .focusplugin archive.
/// This is the first thing the host reads before touching any DLL, so it can
/// validate compatibility without loading any foreign code.
/// </summary>
public sealed class PluginManifest
{
    // ── Identity ──────────────────────────────────────────────────────────

    /// <summary>
    /// Unique reverse-DNS identifier that must never change across versions.
    /// Example: "com.yourname.myplugin"
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Human-readable display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Semantic version string. Example: "1.0.0"</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Author or organisation name.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    /// <summary>Short description (≤ 140 chars) of what the plugin does.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    // ── Compatibility ──────────────────────────────────────────────────────

    /// <summary>
    /// Minimum host version required. If the running FocusTracker is older, the
    /// plugin is rejected before any DLL is loaded.
    /// </summary>
    [JsonPropertyName("minHostVersion")]
    public string MinHostVersion { get; set; } = "1.0.0";

    // ── Entry point ────────────────────────────────────────────────────────

    /// <summary>
    /// Filename of the plugin DLL inside the archive, e.g. "MyPlugin.dll".
    /// The file must be at the root of the ZIP (no sub-folder prefix needed).
    /// </summary>
    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; set; } = "";

    // ── Deserialization ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to deserialise a manifest from a JSON string.
    /// Returns null if the JSON is malformed.
    /// </summary>
    public static PluginManifest? TryLoad(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    // ── Validation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validates all required fields and checks host-version compatibility.
    /// </summary>
    /// <param name="hostVersion">The running host's version.</param>
    /// <returns>
    /// <c>null</c> when valid; otherwise a human-readable error message.
    /// </returns>
    public string? Validate(Version hostVersion)
    {
        if (string.IsNullOrWhiteSpace(Id))
            return "Manifest 'id' is missing or empty.";

        if (!IsValidId(Id))
            return $"Manifest 'id' '{Id}' is not a valid reverse-DNS identifier " +
                   "(only letters, digits, dots, hyphens, underscores allowed).";

        if (string.IsNullOrWhiteSpace(Name))
            return "Manifest 'name' is missing or empty.";

        if (string.IsNullOrWhiteSpace(Version))
            return "Manifest 'version' is missing or empty.";

        if (string.IsNullOrWhiteSpace(EntryAssembly))
            return "Manifest 'entryAssembly' is missing or empty.";

        if (!System.Version.TryParse(MinHostVersion, out var minVer))
            return $"Manifest 'minHostVersion' '{MinHostVersion}' is not a valid version string.";

        if (hostVersion < minVer)
            return $"Plugin requires FocusTracker ≥ {MinHostVersion} (current: {hostVersion}). " +
                   "Please update the host application.";

        return null; // all good
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
        foreach (char c in id)
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
                return false;
        return true;
    }
}
