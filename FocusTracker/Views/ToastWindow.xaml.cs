using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusTracker.Plugins;
using WpfButton = System.Windows.Controls.Button;
using WpfColor  = System.Windows.Media.Color;

namespace FocusTracker.Views;

public enum ToastKind { Success, Info, Warning, Error }

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer? _timer;
    private readonly bool             _isPersistent;

    // ── Constructor ───────────────────────────────────────────────────────

    private ToastWindow(
        string                           title,
        string                           message,
        ToastKind                        kind,
        bool                             persistent,
        IReadOnlyList<PluginToastAction>? actions)
    {
        InitializeComponent();
        _isPersistent = persistent;

        TxtTitle.Text   = title;
        TxtMessage.Text = message;

        ApplyKind(kind);

        if (persistent)
        {
            BtnClose.Visibility = Visibility.Visible;
            BuildActions(actions);
        }

        Loaded += OnLoaded;

        if (!persistent)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            _timer.Tick += (_, _) => { _timer.Stop(); FadeOut(); };
        }
    }

    // ── Appearance helpers ────────────────────────────────────────────────

    private void ApplyKind(ToastKind kind)
    {
        switch (kind)
        {
            case ToastKind.Success:
                IconBorder.Background = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x22, 0x00));
                TxtIcon.Text          = "✓";
                TxtIcon.Foreground    = new SolidColorBrush(WpfColor.FromRgb(0xC8, 0xFF, 0x00));
                break;
            case ToastKind.Warning:
                IconBorder.Background = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x1A, 0x00));
                TxtIcon.Text          = "⚠";
                TxtIcon.Foreground    = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xAA, 0x44));
                break;
            case ToastKind.Error:
                IconBorder.Background = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x00, 0x08));
                TxtIcon.Text          = "✕";
                TxtIcon.Foreground    = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x4D, 0x6A));
                break;
            default: // Info
                IconBorder.Background = new SolidColorBrush(WpfColor.FromRgb(0x00, 0x18, 0x2A));
                TxtIcon.Text          = "ℹ";
                TxtIcon.Foreground    = new SolidColorBrush(WpfColor.FromRgb(0x4D, 0xA6, 0xFF));
                break;
        }
    }

    private void BuildActions(IReadOnlyList<PluginToastAction>? actions)
    {
        if (actions == null || actions.Count == 0) return;

        ActionsPanel.Visibility = Visibility.Visible;
        foreach (var action in actions)
        {
            var a = action; // capture
            var btn = new WpfButton
            {
                Content         = a.Label,
                Style           = (Style)FindResource("OutlineButton"),
                Padding         = new Thickness(12, 6, 12, 6),
                Margin          = new Thickness(0, 0, 8, 0),
                FontSize        = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) =>
            {
                try { a.OnClicked(); } catch { /* plugin handler fault */ }
                FadeOut();
            };
            ActionsPanel.Children.Add(btn);
        }
    }

    // ── Layout & animation ────────────────────────────────────────────────

    private void OnLoaded(object s, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top  = screen.Bottom - ActualHeight - 20;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        RootBorder.BeginAnimation(OpacityProperty, fadeIn);
        _timer?.Start();
    }

    private void BtnClose_Click(object s, RoutedEventArgs e) => FadeOut();

    private void FadeOut()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => Close();
        RootBorder.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ── Static factory helpers ────────────────────────────────────────────

    /// <summary>
    /// Shows a temporary toast that auto-dismisses. Plays the system sound if the
    /// user has notification sounds enabled.
    /// </summary>
    public static void ShowTemporary(string title, string message,
                                     ToastKind kind = ToastKind.Success)
    {
        var toast = new ToastWindow(title, message, kind,
                                    persistent: false, actions: null);
        toast.Show();

        if (App.Settings.NotificationSound)
            TryPlaySound(SystemSounds.Asterisk);
    }

    /// <summary>
    /// Shows a persistent toast that stays until dismissed. Plays the system sound
    /// if the user has notification sounds enabled.
    /// </summary>
    public static void ShowPersistent(string title, string message,
                                      ToastKind kind = ToastKind.Success,
                                      IReadOnlyList<PluginToastAction>? actions = null)
    {
        var toast = new ToastWindow(title, message, kind,
                                    persistent: true, actions: actions);
        toast.Show();

        if (App.Settings.NotificationSound)
            TryPlaySound(SystemSounds.Asterisk);
    }

    /// <summary>
    /// Legacy helper — temporary toast, respects sound setting.
    /// Kept for internal host usage (e.g. install success).
    /// </summary>
    public static void Show(string title, string message,
                             ToastKind kind = ToastKind.Success)
        => ShowTemporary(title, message, kind);

    private static void TryPlaySound(System.Media.SystemSound sound)
    {
        try { sound.Play(); }
        catch { /* audio device unavailable */ }
    }
}
