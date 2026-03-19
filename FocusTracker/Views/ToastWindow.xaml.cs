using System.Media;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FocusTracker.Views;

public enum ToastKind { Success, Info, Warning, Error }

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ToastWindow(string title, string message, ToastKind kind = ToastKind.Success)
    {
        InitializeComponent();
        TxtTitle.Text   = title;
        TxtMessage.Text = message;

        // Style by kind
        switch (kind)
        {
            case ToastKind.Success:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1A, 0x22, 0x00));
                TxtIcon.Text       = "✓";
                TxtIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xC8, 0xFF, 0x00));
                break;
            case ToastKind.Warning:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2A, 0x1A, 0x00));
                TxtIcon.Text       = "⚠";
                TxtIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x44));
                break;
            case ToastKind.Error:
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2A, 0x00, 0x08));
                TxtIcon.Text       = "✕";
                TxtIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x6A));
                break;
            default:
                TxtIcon.Text = "ℹ";
                break;
        }

        Loaded += OnLoaded;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _timer.Tick += (_, _) => { _timer.Stop(); FadeOut(); };
    }

    private void OnLoaded(object s, RoutedEventArgs e)
    {
        // Position bottom-right of owner or screen
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top  = screen.Bottom - ActualHeight - 20;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        RootBorder.BeginAnimation(OpacityProperty, fadeIn);
        _timer.Start();
    }

    private void FadeOut()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => Close();
        RootBorder.BeginAnimation(OpacityProperty, fadeOut);
    }

    // Static helper — show and forget
    public static void Show(string title, string message, ToastKind kind = ToastKind.Success)
    {
        var toast = new ToastWindow(title, message, kind);
        toast.Show();

        // Play system sound if enabled in settings
        if (App.Settings.NotificationSound)
        {
            try
            {
                SystemSounds.Asterisk.Play();
            }
            catch { /* audio device may be unavailable */ }
        }
    }
}
