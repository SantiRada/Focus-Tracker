// ── FocusTracker.PluginSDK — PluginContribution.cs ───────────────────────────
// Models used by plugins to contribute buttons, cards, and notifications to
// the host application's screens.
// ─────────────────────────────────────────────────────────────────────────────

namespace FocusTracker.Plugins;

// ═══════════════════════════════════════════════════════════════════════════
// SCREEN TARGETS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// The host screen where a plugin can contribute buttons or content cards.
/// </summary>
public enum PluginScreenTarget
{
    /// <summary>The main home / start page.</summary>
    Home,
    /// <summary>The usage dashboard page.</summary>
    Dashboard,
    /// <summary>The sessions list page.</summary>
    Sessions,
    /// <summary>The projects page.</summary>
    Projects,
    /// <summary>The live-tracking overlay shown while a session is active.</summary>
    LiveTracking,
}

// ═══════════════════════════════════════════════════════════════════════════
// BUTTON CONTRIBUTIONS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Visual style of a plugin-contributed button.
/// Each style maps 1-to-1 to an existing host button style.
/// </summary>
public enum PluginButtonStyle
{
    /// <summary>Filled with the product accent colour (lime-green). Use for main CTAs.</summary>
    Primary,
    /// <summary>Transparent background with grey border. General-purpose default.</summary>
    Default,
    /// <summary>Like Default but tinted with the unfocus orange. Use for secondary actions.</summary>
    Secondary,
    /// <summary>Like Default but tinted with the link blue. Use for informational actions.</summary>
    Tertiary,
    /// <summary>Red-tinted border. Use for destructive / stop actions.</summary>
    Danger,
}

/// <summary>
/// Describes a button that a plugin contributes to a host screen.
/// Create one per button and pass it to <see cref="IPluginContext.RegisterButton"/>.
/// </summary>
public sealed class PluginButtonContribution
{
    // ── Placement ──────────────────────────────────────────────────────────

    /// <summary>The screen where this button will appear. Required.</summary>
    public required PluginScreenTarget Screen { get; init; }

    // ── Appearance ─────────────────────────────────────────────────────────

    /// <summary>Button text. Required.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Optional Fluent Icons / Segoe MDL2 Assets unicode character (e.g. <c>"\uE768"</c>).
    /// When set, the icon is displayed to the left of the label at the standard size.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Visual style. Defaults to <see cref="PluginButtonStyle.Default"/>.</summary>
    public PluginButtonStyle Style { get; init; } = PluginButtonStyle.Default;

    // ── Behaviour ──────────────────────────────────────────────────────────

    /// <summary>Called on the UI thread when the button is clicked. Required.</summary>
    public required Action OnClicked { get; init; }

    /// <summary>
    /// Optional callback that the host calls periodically on the UI thread to
    /// decide whether to show or hide this button.
    /// Return <c>true</c> = visible, <c>false</c> = collapsed.
    /// When <c>null</c> (default) the button is always visible.
    /// </summary>
    public Func<bool>? GetIsVisible { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// CARD CONTRIBUTIONS (Home screen)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single row inside a <see cref="PluginCardContribution"/>.
/// </summary>
public sealed class PluginCardRow
{
    /// <summary>Left-aligned label / key text. Required.</summary>
    public required string Label { get; init; }

    /// <summary>Optional right-aligned value text.</summary>
    public string? Value { get; init; }

    /// <summary>
    /// When <c>true</c>, this row is rendered as a section header
    /// (bold, muted colour, no value column).
    /// </summary>
    public bool IsHeader { get; init; } = false;
}

/// <summary>
/// Describes a card that a plugin contributes to the Home screen.
/// Create one and pass it to <see cref="IPluginContext.RegisterHomeCard"/>.
/// </summary>
public sealed class PluginCardContribution
{
    // ── Header ─────────────────────────────────────────────────────────────

    /// <summary>Card title shown in the header. Required.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Optional Fluent Icons / Segoe MDL2 Assets icon shown next to the title.
    /// </summary>
    public string? Icon { get; init; }

    // ── Content ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called on the UI thread to produce the card rows.
    /// Return an empty list to render an empty-state message.
    /// Required.
    /// </summary>
    public required Func<IReadOnlyList<PluginCardRow>> GetRows { get; init; }

    // ── Interaction ────────────────────────────────────────────────────────

    /// <summary>
    /// Optional callback invoked when the user clicks anywhere on the card.
    /// When non-null the card renders with a hand cursor and a hover highlight.
    /// Typical use: navigate to this plugin's settings tab via
    /// <c>IPluginContext.NavigateToPluginSettings()</c>.
    /// </summary>
    public Action? OnCardClicked { get; init; }

    // ── Hot-reload ─────────────────────────────────────────────────────────

    /// <summary>
    /// When greater than zero the host refreshes this card automatically every
    /// <c>AutoRefreshSeconds</c> seconds. Set to <c>0</c> (default) for a
    /// static card that only renders once.
    /// </summary>
    public int AutoRefreshSeconds { get; init; } = 0;
}

// ═══════════════════════════════════════════════════════════════════════════
// NOTIFICATION MODELS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Visual kind of a notification toast (controls icon and accent colour).
/// </summary>
public enum PluginToastKind { Success, Info, Warning, Error }

/// <summary>
/// An action button inside a persistent notification toast.
/// Only supported for persistent toasts (ignored on temporary ones).
/// </summary>
public sealed class PluginToastAction
{
    /// <summary>Button label. Required.</summary>
    public required string Label { get; init; }

    /// <summary>Called on the UI thread when the button is clicked. Required.</summary>
    public required Action OnClicked { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// USER HELP  (required per-plugin documentation)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single section inside a plugin's user-help page.
/// </summary>
public sealed class PluginHelpSection
{
    /// <summary>Section heading shown in bold. Required.</summary>
    public required string Heading { get; init; }

    /// <summary>Body text for this section. Supports plain text only. Required.</summary>
    public required string Body { get; init; }
}

/// <summary>
/// The complete user-help content for a plugin.
/// Returned by <see cref="IFocusPlugin.GetUserHelp"/> and rendered by the host
/// in the Plugin Manager's "Ayuda" screen.
///
/// Every plugin MUST return a non-null instance with a non-empty
/// <see cref="Summary"/>. Plugins that do not implement
/// <see cref="IFocusPlugin.GetUserHelp"/> will not compile.
/// </summary>
public sealed class PluginHelpContent
{
    /// <summary>
    /// One-paragraph overview of what the plugin does.
    /// Shown at the top of the help screen before the sections. Required.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Ordered list of help sections. May be empty for simple plugins.
    /// </summary>
    public IReadOnlyList<PluginHelpSection> Sections { get; init; } =
        Array.Empty<PluginHelpSection>();
}

// ═══════════════════════════════════════════════════════════════════════════
// NAV SECTION CONTRIBUTIONS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single action button rendered in the top-right area of a plugin-contributed
/// nav screen (grey outline style, same zone as the native "Guía rápida" button).
/// </summary>
public sealed class PluginNavActionButton
{
    /// <summary>Button text. Required.</summary>
    public required string Label { get; init; }

    /// <summary>Optional Fluent Icons unicode character shown left of the label.</summary>
    public string? Icon { get; init; }

    /// <summary>Called on the UI thread when the button is clicked. Required.</summary>
    public required Action OnClicked { get; init; }

    /// <summary>
    /// Optional callback polled by the host to enable/disable the button.
    /// Return <c>false</c> to disable. <c>null</c> = always enabled.
    /// </summary>
    public Func<bool>? GetIsEnabled { get; init; }
}

/// <summary>
/// Contributes a new section to the left-side navigation bar.
/// Clicking the nav item opens a full-page screen rendered by the plugin.
/// Register via <see cref="IPluginContext.RegisterNavSection"/>.
/// </summary>
public sealed class PluginNavSectionContribution
{
    // ── Nav item ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fluent Icons / Segoe MDL2 Assets unicode character for the sidebar icon.
    /// Example: <c>"\uE768"</c>.  Required.
    /// </summary>
    public required string Icon { get; init; }

    /// <summary>
    /// Sidebar label text. Required. Maximum 20 characters to avoid overflow.
    /// </summary>
    public required string Label { get; init; }

    // ── Screen header ───────────────────────────────────────────────────────

    /// <summary>Large title shown at the top of the screen. Required.</summary>
    public required string Title { get; init; }

    /// <summary>Optional subtitle shown below the title in muted text.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// When <c>true</c> a back arrow is shown left of the title.
    /// It always navigates the user back to the Home (Inicio) screen.
    /// </summary>
    public bool ShowBackButton { get; init; } = false;

    // ── Top-right action buttons ────────────────────────────────────────────

    /// <summary>
    /// Buttons placed in the top-right corner of the screen header
    /// (grey outline style, same zone as the native "Guía rápida" button).
    /// </summary>
    public IReadOnlyList<PluginNavActionButton> ActionButtons { get; init; }
        = Array.Empty<PluginNavActionButton>();

    // ── Top-bar content ─────────────────────────────────────────────────────

    /// <summary>
    /// Optional content placed in the top-bar zone (centre-top area, typically
    /// used for search fields, filter dropdowns, and similar input controls).
    /// Must return a WPF <c>FrameworkElement</c>; the SDK types this as
    /// <c>object</c> to avoid a WPF dependency in the SDK assembly.
    /// Called once when the screen is first shown.
    /// </summary>
    public Func<object>? BuildTopBar { get; init; }

    // ── Main content ────────────────────────────────────────────────────────

    /// <summary>
    /// Factory for the main screen content placed inside a <c>ScrollViewer</c>.
    /// Must return a WPF <c>FrameworkElement</c>; the SDK types this as
    /// <c>object</c> to avoid a WPF dependency in the SDK assembly.
    /// Called once when the screen is first shown. Required.
    /// </summary>
    public required Func<object> BuildContent { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// PROJECT EDIT TAB CONTRIBUTIONS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Contributes an extra tab to the project creation / editing screen.
/// The host moves the existing settings into a "General" tab and adds one
/// tab per plugin that calls <see cref="IPluginContext.RegisterProjectTab"/>.
/// </summary>
public sealed class PluginProjectTabContribution
{
    /// <summary>
    /// Tab label shown in the tab bar (typically the plugin's display name).
    /// Required.
    /// </summary>
    public required string TabName { get; init; }

    /// <summary>
    /// Factory for the tab content.
    /// <para>
    /// Parameter: <c>int? projectId</c> — <c>null</c> when the user is creating
    /// a new project; the existing project ID when editing.
    /// </para>
    /// Must return a WPF <c>FrameworkElement</c>; typed as <c>object</c> to
    /// avoid a WPF dependency in the SDK assembly. Required.
    /// </summary>
    public required Func<int?, object> BuildContent { get; init; }

    /// <summary>
    /// Called on the UI thread immediately after the host saves the project.
    /// Parameter: the ID of the newly created or updated project.
    /// Use this to persist any settings the tab collected.
    /// </summary>
    public Action<int>? OnProjectSaved { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// HELP SECTION CONTRIBUTIONS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Contributes an extra section to the native "Ayuda" screen, displayed below
/// all built-in content so native help retains visual prominence.
/// Register via <see cref="IPluginContext.RegisterHelpSection"/>.
/// </summary>
public sealed class PluginHelpContribution
{
    /// <summary>
    /// Small grey label shown above the content card (e.g. "MI PLUGIN").
    /// When <c>null</c> or empty the host uses the plugin's display name.
    /// </summary>
    public string? ExternalTitle { get; init; }

    /// <summary>
    /// Factory for the read-only content card.
    /// ⚠️  The returned element MUST NOT contain interactive controls
    /// (no <c>TextBox</c>, <c>Button</c>, <c>CheckBox</c>, etc.).
    /// Must return a WPF <c>FrameworkElement</c>; typed as <c>object</c> to
    /// avoid a WPF dependency in the SDK assembly. Required.
    /// </summary>
    public required Func<object> BuildContent { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// PROJECT-LIST OVERLAY CONTRIBUTIONS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single interactive button contributed to the project-list overlay strip.
/// </summary>
public sealed class PluginProjectOverlayButton
{
    /// <summary>Button text. Required.</summary>
    public required string Label { get; init; }

    /// <summary>Optional Fluent Icons unicode character shown left of the label.</summary>
    public string? Icon { get; init; }

    /// <summary>Visual style. Defaults to <see cref="PluginButtonStyle.Default"/>.</summary>
    public PluginButtonStyle Style { get; init; } = PluginButtonStyle.Default;

    /// <summary>Called on the UI thread when the button is clicked. Required.</summary>
    public required Action OnClicked { get; init; }

    /// <summary>Optional visibility callback. <c>null</c> = always visible.</summary>
    public Func<bool>? GetIsVisible { get; init; }

    /// <summary>Optional enabled callback. <c>null</c> = always enabled.</summary>
    public Func<bool>? GetIsEnabled { get; init; }

    /// <summary>
    /// Optional dynamic label callback polled by the host on each render cycle.
    /// When non-null the returned string overrides <see cref="Label"/>.
    /// </summary>
    public Func<string>? GetLabel { get; init; }
}

/// <summary>
/// Contributes interactive buttons or arbitrary content displayed in a strip
/// above the project list on the Projects screen — useful for quick
/// activate/deactivate toggles without opening the project editor.
/// Register via <see cref="IPluginContext.RegisterProjectOverlay"/>.
/// </summary>
public sealed class PluginProjectOverlayContribution
{
    /// <summary>
    /// Structured buttons rendered in the overlay strip using the host's button
    /// styles. Used for simple quick-action buttons (most common case).
    /// </summary>
    public IReadOnlyList<PluginProjectOverlayButton> Buttons { get; init; }
        = Array.Empty<PluginProjectOverlayButton>();

    /// <summary>
    /// Optional arbitrary WPF content for the overlay strip.
    /// When non-null this takes priority over <see cref="Buttons"/>.
    /// Must return a WPF <c>FrameworkElement</c>; typed as <c>object</c> to
    /// avoid a WPF dependency in the SDK assembly.
    /// </summary>
    public Func<object>? BuildContent { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// SOUND
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Windows built-in system sounds available to plugins.
/// Maps to <see cref="System.Media.SystemSounds"/> members.
/// </summary>
public enum WindowsSystemSound
{
    /// <summary>The system "Asterisk" / information sound.</summary>
    Asterisk,
    /// <summary>The system "Beep" / generic beep.</summary>
    Beep,
    /// <summary>The system "Exclamation" / warning sound.</summary>
    Exclamation,
    /// <summary>The system "Hand" / critical-stop sound.</summary>
    Hand,
    /// <summary>The system "Question" sound.</summary>
    Question,
}
