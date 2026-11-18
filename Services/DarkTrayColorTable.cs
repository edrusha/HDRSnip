using System.Windows.Forms;
using Color = System.Drawing.Color;

namespace HDRSnip.Services;

internal sealed class DarkTrayColorTable : ProfessionalColorTable
{
    private static readonly Color Background = Color.FromArgb(23, 28, 36);
    private static readonly Color Hover = Color.FromArgb(37, 45, 58);
    private static readonly Color Border = Color.FromArgb(42, 51, 65);
    private static readonly Color Separator = Color.FromArgb(48, 59, 75);

    public override Color ToolStripDropDownBackground => Background;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color MenuBorder => Border;
    public override Color MenuItemBorder => Border;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Background;
    public override Color MenuItemPressedGradientMiddle => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color SeparatorDark => Separator;
    public override Color SeparatorLight => Background;
    public override Color ButtonSelectedHighlight => Hover;
    public override Color ButtonSelectedHighlightBorder => Border;
    public override Color ButtonPressedHighlight => Hover;
    public override Color ButtonPressedHighlightBorder => Border;
}

internal sealed class DarkTrayRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Text = Color.FromArgb(242, 245, 250);
    private static readonly Color DisabledText = Color.FromArgb(120, 132, 150);

    public DarkTrayRenderer() : base(new DarkTrayColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : DisabledText;
        base.OnRenderItemText(e);
    }
}
