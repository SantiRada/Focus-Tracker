using System.Windows;

namespace FocusTracker.Views;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog(string title, string message, string confirmText = "Eliminar")
    {
        InitializeComponent();
        TxtTitle.Text   = title;
        TxtMessage.Text = message;
        BtnConfirm.Content = confirmText;

        // Enable dark title bar
        Loaded += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Helpers.WinApi.EnableDarkTitleBar(hwnd);
        };
    }

    private void BtnConfirm_Click(object s, RoutedEventArgs e) { Confirmed = true;  Close(); }
    private void BtnCancel_Click(object s, RoutedEventArgs e)  { Confirmed = false; Close(); }

    /// <summary>Show dialog centered on owner. Returns true if confirmed.</summary>
    public static bool Show(Window owner, string title, string message, string confirmText = "Eliminar", bool danger = false)
    {
        var dlg = new ConfirmDialog(title, message, confirmText) { Owner = owner };
        if (danger)
        {
            dlg.IconBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 0, 0));
            dlg.TxtIcon.Text = "☢";
        }
        dlg.ShowDialog();
        return dlg.Confirmed;
    }
}
