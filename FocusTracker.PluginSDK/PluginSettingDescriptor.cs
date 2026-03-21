// ── FocusTracker.PluginSDK — PluginSettingDescriptor.cs ──────────────────────
namespace FocusTracker.Plugins;

/// <summary>Defines how the user interacts with a plugin-contributed setting item.</summary>
public enum PluginSettingType
{
    /// <summary>An on/off toggle switch.</summary>
    Toggle,
    /// <summary>A single-line text input field.</summary>
    TextInput,
    /// <summary>A path display + "Browse…" button for picking a file.</summary>
    FilePicker,
    /// <summary>A path display + "Browse…" button for picking a folder.</summary>
    FolderPicker,
    /// <summary>A labelled action button. Supports danger (red) style.</summary>
    Button,
    /// <summary>A dropdown selector with a fixed list of string options.</summary>
    Select,
    /// <summary>
    /// A read-only section header / divider. Renders the Title in bold and the
    /// Description as muted text below it, with no interactive control.
    /// Use to visually group settings within a tab.
    /// </summary>
    Label,
    /// <summary>
    /// A text input with a live-filtered suggestion dropdown.
    /// The host calls <see cref="PluginSettingDescriptor.AutoCompleteGetSuggestions"/>
    /// on every keystroke and renders up to eight results below the field.
    /// Committing a suggestion (click or Enter) calls
    /// <see cref="PluginSettingDescriptor.OnAutoCompleteCommitted"/>.
    /// Use <see cref="PluginSettingDescriptor.TextPlaceholder"/> for placeholder text and
    /// <see cref="PluginSettingDescriptor.TextDefault"/> for the initial value.
    /// </summary>
    AutoComplete,
}

/// <summary>
/// Describes a single setting item contributed by a plugin to the host's Settings page.
/// Create one per setting and pass it to <see cref="IPluginContext.RegisterSetting"/>.
/// </summary>
public sealed class PluginSettingDescriptor
{
    // ── Placement ──────────────────────────────────────────────────────────
    /// <summary>
    /// Tab name in the Settings page. Created automatically if it does not exist.
    /// Required.
    /// </summary>
    public required string TabName { get; init; }

    // ── Label ──────────────────────────────────────────────────────────────
    /// <summary>Bold heading shown above the control. Required.</summary>
    public required string Title { get; init; }
    /// <summary>Optional muted supporting text below the title.</summary>
    public string? Description { get; init; }

    // ── Type ───────────────────────────────────────────────────────────────
    /// <summary>Controls how the setting is rendered. Required.</summary>
    public required PluginSettingType Type { get; init; }

    // ── Toggle ─────────────────────────────────────────────────────────────
    public bool              ToggleDefault   { get; init; } = false;
    public Action<bool>?     OnToggleChanged { get; init; }

    // ── TextInput ──────────────────────────────────────────────────────────
    public string?           TextPlaceholder  { get; init; }
    public string?           TextDefault      { get; init; }
    public Action<string>?   OnTextCommitted  { get; init; }

    // ── FilePicker / FolderPicker ──────────────────────────────────────────
    /// <summary>Win32 filter string, e.g. "JSON files|*.json|All files|*.*". FilePicker only.</summary>
    public string?           FileFilter       { get; init; }
    public string?           PathDefault      { get; init; }
    public Action<string>?   OnPathSelected   { get; init; }

    // ── Button ─────────────────────────────────────────────────────────────
    public string?           ButtonLabel      { get; init; }
    /// <summary>When true the button is styled red. Use for destructive actions.</summary>
    public bool              ButtonIsDanger   { get; init; } = false;
    /// <summary>When true the button is styled with the secondary (orange) accent colour. Use for primary call-to-action settings buttons.</summary>
    public bool              ButtonIsSecondary { get; init; } = false;
    public Action?           OnButtonClicked  { get; init; }

    // ── Select ─────────────────────────────────────────────────────────────
    public string[]?         SelectOptions       { get; init; }
    public int               SelectDefaultIndex  { get; init; } = 0;
    public Action<string>?   OnSelectChanged     { get; init; }

    // ── AutoComplete ────────────────────────────────────────────────────────
    /// <summary>
    /// Called on every keystroke with the current query string.
    /// Return up to eight suggestion display strings; return null or empty to suppress the popup.
    /// Must be fast (≤ 10 ms) — it runs on the UI thread.
    /// </summary>
    public Func<string, IReadOnlyList<string>>? AutoCompleteGetSuggestions { get; init; }

    /// <summary>
    /// Called when the user confirms a value — either by clicking a suggestion from the
    /// dropdown or by pressing Enter in the text field.
    /// Receives the exact suggestion string that was selected (or the raw typed text).
    /// </summary>
    public Action<string>? OnAutoCompleteCommitted { get; init; }
}
