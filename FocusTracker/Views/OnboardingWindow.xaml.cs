using System.Windows;
using Microsoft.Win32;

namespace FocusTracker.Views;

public partial class OnboardingWindow : Window
{
    private int _step = 1;

    public OnboardingWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Helpers.WinApi.EnableDarkTitleBar(hwnd);
        };
    }

    private void Next_Click(object s, RoutedEventArgs e)
    {
        _step++;
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Finish_Click(object s, RoutedEventArgs e)
    {
        // Mark onboarding as completed
        MarkCompleted();
        Close();
    }

    // ── Static helpers ────────────────────────────────────────────────────

    private const string RegKey   = @"Software\FocusTracker";
    private const string RegValue = "OnboardingDone";

    public static bool IsCompleted()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey);
            return key?.GetValue(RegValue) is int v && v == 1;
        }
        catch { return false; }
    }

    public static void MarkCompleted()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey);
            key?.SetValue(RegValue, 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static void ResetStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, true);
            key?.DeleteValue(RegValue, false);
        }
        catch { }
    }
}
