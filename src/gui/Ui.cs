using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LockscreenGui;

/// <summary>
/// UI 视觉层（v3.1）：统一色板、共享字体与自绘控件。
/// 只负责外观，不含任何业务逻辑。色板以浅色现代风为基准，主色 #2563EB 与应用图标呼应。
/// 注意：所有字体为静态共享实例（跨控件复用），禁止每次 new Font —— GDI 句柄泄漏（历史坑）。
/// </summary>
internal static class Ui
{
    // ---- 色板（浅色现代风）----
    public static readonly Color WindowBg   = Color.FromArgb(0xF3, 0xF4, 0xF6);  // 窗体/页面底色
    public static readonly Color CardBg     = Color.White;                       // 卡片底
    public static readonly Color CardBorder = Color.FromArgb(0xE5, 0xE7, 0xEB);  // 卡片描边
    public static readonly Color TitleText  = Color.FromArgb(0x11, 0x18, 0x27);  // 卡片标题
    public static readonly Color BodyText   = Color.FromArgb(0x37, 0x41, 0x51);  // 正文
    public static readonly Color SubText    = Color.FromArgb(0x6B, 0x72, 0x80);  // 次要文字/描述
    public static readonly Color Primary     = Color.FromArgb(0x25, 0x63, 0xEB); // 主色
    public static readonly Color PrimaryHover = Color.FromArgb(0x1D, 0x4E, 0xD8);
    public static readonly Color PrimaryDown  = Color.FromArgb(0x1E, 0x40, 0xAF);
    public static readonly Color PrimarySoft  = Color.FromArgb(0xEF, 0xF6, 0xFF); // 主色淡底（hover）
    public static readonly Color PrimaryLine  = Color.FromArgb(0x93, 0xC5, 0xFD); // 主色淡边（hover 边框）
    public static readonly Color Success    = Color.FromArgb(0x05, 0x96, 0x69);  // 操作成功反馈色
    public static readonly Color Warning    = Color.FromArgb(0xDC, 0x26, 0x26);  // 警告/异常
    public static readonly Color BtnBorder  = Color.FromArgb(0xD1, 0xD5, 0xDB);  // 普通按钮描边
    public static readonly Color HoverFill  = Color.FromArgb(0xE9, 0xEA, 0xEC);  // 灰系 hover 底

    // ---- 共享字体（静态复用，防 GDI 句柄泄漏）----
    public static readonly Font FontBody   = new("Microsoft YaHei UI", 9.5F);
    public static readonly Font FontBold   = new("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
    public static readonly Font FontTitle  = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    public static readonly Font FontStatus = new("Microsoft YaHei UI", 9F);

    /// <summary>生成圆角矩形路径（调用方负责 Dispose）。</summary>
    public static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// 自绘圆角按钮：完全接管绘制（抗锯齿圆角 + 三态颜色反馈：常态/hover/按下 + 禁用态）。
/// 三种风格：
///   Neutral —— 白底描边（预设值/普通操作按钮）
///   Primary —— 主色实底白字（「应用」等主动作）
///   Ghost   —— 透明底灰字（标签页按钮），Selected=true 时变主色实底白字
/// </summary>
internal sealed class RoundedButton : Button
{
    public enum Kinds { Neutral, Primary, Ghost }

    public Kinds Kind { get; set; } = Kinds.Neutral;
    public float Radius { get; set; } = 6F;

    bool _hover, _down;

    public RoundedButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;          // 阻止系统主题绘制
        FlatAppearance.BorderSize = 0;
        TabStop = false;                      // 大量预设按钮不该吃 Tab 焦点（与旧版一致）
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 8, 0);
    }

    // 尺寸 = 文本 + 对称内边距，替代旧版 AutoSize
    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + 26, t.Height + 12);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 先把整个客户区清成父容器底色（不能用 OnPaintBackground：
        // ButtonBase 对背景擦除有自己的处理，残留脏像素会在圆角外四角留下黑块）
        g.Clear(Parent?.BackColor ?? Ui.WindowBg);

        Color fill, text, border;
        if (!Enabled)
        {
            fill = Color.FromArgb(0xF3, 0xF4, 0xF6);
            text = Color.FromArgb(0x9C, 0xA3, 0xAF);
            border = Ui.CardBorder;
        }
        else
        {
            (fill, text, border) = Kind switch
            {
                Kinds.Primary
                    => _down ? (Ui.PrimaryDown, Color.White, Ui.PrimaryDown)
                     : _hover ? (Ui.PrimaryHover, Color.White, Ui.PrimaryHover)
                     : (Ui.Primary, Color.White, Ui.Primary),
                Kinds.Ghost
                    => Selected ? (Ui.Primary, Color.White, Ui.Primary)
                     : _hover ? (Ui.HoverFill, Ui.TitleText, Ui.HoverFill)
                     : (Parent?.BackColor ?? Ui.WindowBg, Ui.SubText, Parent?.BackColor ?? Ui.WindowBg),
                _ // Neutral
                    => _down ? (Ui.PrimarySoft, Ui.PrimaryDown, Ui.PrimaryLine)
                     : _hover ? (Color.White, Ui.Primary, Ui.PrimaryLine)
                     : (Ui.CardBg, Ui.BodyText, Ui.BtnBorder),
            };
        }

        float r = Math.Min(Radius, Math.Min(Width, Height) / 2f);
        using var path = Ui.RoundedRect(0.5f, 0.5f, Width - 1, Height - 1, r);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var p = new Pen(border))
            if (border != fill) g.DrawPath(p, path);

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    // Ghost 按钮的选中态（标签页用）
    public bool Selected { get; set; }
}

/// <summary>
/// 圆角卡片容器：替代旧版 GroupBox（Win11 下 GroupBox 视觉陈旧且标题有 14px 上溢裁切问题）。
/// 自绘：白色圆角底 + 1px 描边 + 左侧主色竖条 + 加粗标题。
/// 标题由本控件绘制，不占用子控件布局；子控件区从 Padding 顶部开始。
/// </summary>
internal sealed class CardPanel : Panel
{
    public string CardTitle { get; set; } = "";

    public CardPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;   // 圆角外的四角露出父容器底色
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // 顶部 46px = 标题区（绘制高度约 30px）+ 呼吸空间；旧 GroupBox 的 Padding.Top=22 标题上溢
        // 裁切问题在这里不存在——标题完全画在自己的空间内
        Padding = new Padding(16, 46, 16, 16);
        Margin = new Padding(0, 0, 0, 14);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Ui.RoundedRect(0.5f, 0.5f, Width - 1, Height - 1, 10);
        using (var b = new SolidBrush(Ui.CardBg)) g.FillPath(b, path);
        using (var p = new Pen(Ui.CardBorder)) g.DrawPath(p, path);

        // 标题：主色竖条 + 深色加粗文字
        using (var bar = Ui.RoundedRect(16, 15, 4, 18, 2))
        using (var bb = new SolidBrush(Ui.Primary)) g.FillPath(bb, bar);
        TextRenderer.DrawText(g, CardTitle, Ui.FontTitle, new Rectangle(28, 10, Width - 40, 28),
            Ui.TitleText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>现代菜单/状态栏渲染器：白底、淡蓝 hover、细分割线，替换系统灰蓝默认配色。</summary>
internal sealed class ModernRenderer : ToolStripProfessionalRenderer
{
    public ModernRenderer() : base(new Palette()) { }

    sealed class Palette : ProfessionalColorTable
    {        public override Color MenuStripGradientBegin => Color.White;
        public override Color MenuStripGradientEnd => Color.White;
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color MenuItemSelected => Ui.PrimarySoft;
        public override Color MenuItemSelectedGradientBegin => Ui.PrimarySoft;
        public override Color MenuItemSelectedGradientEnd => Ui.PrimarySoft;
        public override Color MenuItemPressedGradientBegin => Color.White;
        public override Color MenuItemPressedGradientEnd => Color.White;
        public override Color MenuBorder => Ui.CardBorder;
        public override Color MenuItemBorder => Ui.PrimaryLine;
        public override Color SeparatorDark => Ui.CardBorder;
        public override Color SeparatorLight => Ui.CardBorder;
        public override Color StatusStripGradientBegin => Color.White;
        public override Color StatusStripGradientEnd => Color.White;
        public override Color ToolStripBorder => Color.White;
        public override Color GripLight => Ui.CardBorder;
    }
}
