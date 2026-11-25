using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PowerModeSwitcher.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace PowerModeSwitcher;

/// <summary>
/// Asks once which set of power modes Windows shows, when Auto detection can't tell yet
/// (PLAN.md Phase 8c). The answer becomes the "Power mode options" choice.
/// </summary>
public partial class ModeSetQuestionWindow : Window
{
    private readonly ModeSetService _modeSets;

    public ModeSetQuestionWindow(ModeSetService modeSets)
    {
        InitializeComponent();
        _modeSets = modeSets;

        // The same wording Windows Settings uses, top to bottom as Settings lists them.
        StandardText.Text = string.Join(", ", ModeSet.Standard[Stop.Bottom].Label, ModeSet.Standard[Stop.Middle].Label, ModeSet.Standard[Stop.Top].Label);
        ClassicText.Text = string.Join(", ", ModeSet.Classic[Stop.Bottom].Label, ModeSet.Classic[Stop.Middle].Label, ModeSet.Classic[Stop.Top].Label);

        ApplyTheme(MenuPalette.WindowsUsesDarkMode());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (MenuPalette.WindowsUsesDarkMode()) UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void StandardButton_Click(object sender, RoutedEventArgs e) => Answer(ModeSetChoice.Standard);

    private void ClassicButton_Click(object sender, RoutedEventArgs e) => Answer(ModeSetChoice.Classic);

    private void Answer(ModeSetChoice choice)
    {
        _modeSets.Choose(choice, ModeSetOrigin.Question);
        Close();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Settings could not be opened; the text above still says where to look.
        }
    }

    /// <summary>Colors matching the Windows app color mode, like the right-click menu.</summary>
    private void ApplyTheme(bool dark)
    {
        SetBrush("WindowBrush", dark ? 0x202020 : 0xF9F9F9);
        SetBrush("TextBrush", dark ? 0xFFFFFF : 0x1B1B1B);
        SetBrush("SubtleTextBrush", dark ? 0xC5C5C5 : 0x5F5F5F);
        SetBrush("ButtonBrush", dark ? 0x2D2D2D : 0xFFFFFF);
        SetBrush("ButtonHoverBrush", dark ? 0x383838 : 0xF0F0F0);
        SetBrush("ButtonBorderBrush", dark ? 0x454545 : 0xD4D4D4);
        SetBrush("AccentBrush", dark ? 0x6CB6E8 : 0x0F5C8C);
    }

    private void SetBrush(string key, int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        Resources[key] = brush;
    }

    /// <summary>Dark title bar to match (Windows 11; ignored where unsupported).</summary>
    private static unsafe void UseDarkTitleBar(nint hwnd)
    {
        BOOL dark = true;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, (uint)sizeof(BOOL));
    }
}
