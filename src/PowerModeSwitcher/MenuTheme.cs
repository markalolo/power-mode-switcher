using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PowerModeSwitcher;

/// <summary>Menu colors. There is one set for Windows light mode and one for dark mode.</summary>
public sealed record MenuPalette(Color Background, Color Hover, Color Border, Color Separator, Color Text, Color DisabledText)
{
    public static readonly MenuPalette Light = new(
        Background: Color.FromArgb(0xF9, 0xF9, 0xF9),
        Hover: Color.FromArgb(0xE8, 0xE8, 0xE8),
        Border: Color.FromArgb(0xD4, 0xD4, 0xD4),
        Separator: Color.FromArgb(0xE0, 0xE0, 0xE0),
        Text: Color.FromArgb(0x1B, 0x1B, 0x1B),
        DisabledText: Color.FromArgb(0x8A, 0x8A, 0x8A));

    public static readonly MenuPalette Dark = new(
        Background: Color.FromArgb(0x2B, 0x2B, 0x2B),
        Hover: Color.FromArgb(0x3D, 0x3D, 0x3D),
        Border: Color.FromArgb(0x48, 0x48, 0x48),
        Separator: Color.FromArgb(0x40, 0x40, 0x40),
        Text: Color.FromArgb(0xFF, 0xFF, 0xFF),
        DisabledText: Color.FromArgb(0x9A, 0x9A, 0x9A));

    /// <summary>
    /// True when Windows is set to dark mode for apps
    /// (Settings &gt; Personalization &gt; Colors &gt; Choose your mode).
    /// </summary>
    public static bool WindowsUsesDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Draws the right-click menu in Windows 11 style: flat colors from a MenuPalette,
/// rounded hover highlight, and a check mark that is visible in both light and dark mode.
/// </summary>
public sealed class ThemedMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer(MenuPalette palette)
        : base(new PaletteColorTable(palette))
    {
        Palette = palette;
        RoundedEdges = false;
    }

    public MenuPalette Palette { get; }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Palette.Text : Palette.DisabledText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        float scale = (e.Item.Owner?.DeviceDpi ?? 96) / 96f;
        int inset = (int)Math.Round(3 * scale);
        var bounds = new Rectangle(inset, 1, e.Item.Width - 2 * inset, e.Item.Height - 2);

        using GraphicsPath path = RoundedRectangle(bounds, 4 * scale);
        using var brush = new SolidBrush(Palette.Hover);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        // The built-in check mark is black, which disappears on a dark menu. Draw our own.
        Rectangle r = e.ImageRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;

        float scale = (e.Item.Owner?.DeviceDpi ?? 96) / 96f;
        float left = r.Left + 3 * scale; // keep the mark inside the rounded hover highlight
        using var pen = new Pen(e.Item.Enabled ? Palette.Text : Palette.DisabledText, 1.6f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(left + r.Width * 0.22f, r.Top + r.Height * 0.53f),
            new PointF(left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
        });
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Maps the palette onto the colors the standard menu renderer asks for.</summary>
    private sealed class PaletteColorTable : Forms.ProfessionalColorTable
    {
        private readonly MenuPalette _p;

        public PaletteColorTable(MenuPalette palette)
        {
            _p = palette;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => _p.Background;
        public override Color ImageMarginGradientBegin => _p.Background;
        public override Color ImageMarginGradientMiddle => _p.Background;
        public override Color ImageMarginGradientEnd => _p.Background;
        public override Color MenuBorder => _p.Border;
        public override Color MenuItemBorder => _p.Hover;
        public override Color MenuItemSelected => _p.Hover;
        public override Color MenuItemSelectedGradientBegin => _p.Hover;
        public override Color MenuItemSelectedGradientEnd => _p.Hover;
        public override Color MenuItemPressedGradientBegin => _p.Hover;
        public override Color MenuItemPressedGradientMiddle => _p.Hover;
        public override Color MenuItemPressedGradientEnd => _p.Hover;
        public override Color SeparatorDark => _p.Separator;
        public override Color SeparatorLight => _p.Background;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
