using System.Windows;
using System.Windows.Controls;
using FocusTracker.Models;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfStackPanel  = System.Windows.Controls.StackPanel;
using WpfTextBlock   = System.Windows.Controls.TextBlock;
using WpfSeparator   = System.Windows.Controls.Separator;
using WpfApplication = System.Windows.Application;

namespace FocusTracker.Views;

/// <summary>
/// Custom WPF popup that replaces the WinForms ContextMenuStrip on the tray icon.
/// Shows up to 3 quick-launch project buttons plus a stop-tracking button.
/// </summary>
public partial class TrayPopup : Window
{
    private readonly List<Project> _projects;

    // WPF fires Deactivated as the *first* step of Close() — before Closing/Closed.
    // Without this flag, a button handler calling Close() triggers Deactivated which
    // calls Close() again on a mid-closing window → InvalidOperationException crash.
    private bool _isClosing;

    // ── Constructor ───────────────────────────────────────────────────────
    public TrayPopup(List<Project> projects)
    {
        _projects = projects;
        InitializeComponent();

        BuildProjectButtons();
        RefreshStopButton();

        // Close when the user clicks anywhere outside the popup.
        // Guard prevents the re-entrant Close() when a button already initiated close.
        Deactivated += (_, _) => { if (!_isClosing) ClosePopup(); };
    }

    // ── Safe close: set flag first so Deactivated won't recurse ──────────
    private void ClosePopup()
    {
        _isClosing = true;
        Close();
    }

    // ── Build up-to-3 project launch buttons ─────────────────────────────
    private void BuildProjectButtons()
    {
        ProjectsPanel.Children.Clear();
        var top3 = _projects.Take(3).ToList();

        // Capture the dispatcher now, before ClosePopup() is called inside the handler.
        // After Close(), Dispatcher is technically still valid (it's the app dispatcher)
        // but capturing it upfront is the safest pattern.
        var dispatcher = Dispatcher;

        foreach (var proj in top3)
        {
            var projCapture = proj; // avoid closure-capture of loop variable
            var btn = new WpfButton
            {
                Style   = (Style)FindResource("PopupRowStyle"),
                Padding = new Thickness(14, 9, 14, 9)
            };

            var sp = new WpfStackPanel { Orientation = WpfOrientation.Horizontal };
            sp.Children.Add(new WpfTextBlock
            {
                Text              = "▶  ",
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity           = 0.5
            });
            sp.Children.Add(new WpfTextBlock
            {
                Text              = $"Iniciar: {proj.Name}",
                VerticalAlignment = VerticalAlignment.Center
            });
            btn.Content = sp;

            btn.Click += (_, _) =>
            {
                ClosePopup();
                App.ShowMainWindow();
                // Wait for the window to be fully shown before starting the session
                dispatcher.BeginInvoke(() =>
                {
                    if (WpfApplication.Current.MainWindow is MainWindow mw)
                        mw.StartProjectSession(projCapture);
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };

            ProjectsPanel.Children.Add(btn);
        }

        // Hide the separator after the projects panel when there are no projects
        SepAfterProjects.Visibility = top3.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Enable / disable "Detener tracking" ──────────────────────────────
    private void RefreshStopButton()
    {
        BtnStop.IsEnabled = App.Tracker.IsTracking;
    }

    // ── Button handlers ───────────────────────────────────────────────────
    private void BtnShow_Click(object sender, RoutedEventArgs e)
    {
        ClosePopup();
        App.ShowMainWindow();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        ClosePopup();
        App.Tracker.StopTracking();
        if (App.TrayIcon != null)
            App.TrayIcon.Text = "Focus Tracker — inactivo";
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        ClosePopup();
        App.RealExit();
    }

    // ── Position the popup above the cursor ──────────────────────────────
    /// <summary>
    /// Call after Show() — places the popup so its bottom-right corner
    /// sits at the cursor position (standard tray-menu behaviour).
    /// Clamps to screen bounds so it never goes off-screen.
    /// </summary>
    public void PositionNearCursor()
    {
        UpdateLayout(); // force measure so ActualWidth/Height are valid

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;

        double left = cursor.X - ActualWidth;
        double top  = cursor.Y - ActualHeight;

        // Clamp so the window stays fully on-screen
        if (left < screen.Left)  left = screen.Left;
        if (top  < screen.Top)   top  = screen.Top;
        if (left + ActualWidth  > screen.Right)  left = screen.Right  - ActualWidth;
        if (top  + ActualHeight > screen.Bottom) top  = screen.Bottom - ActualHeight;

        Left = left;
        Top  = top;
    }
}
