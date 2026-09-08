using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 一次性工具：程序化绘制 v3.1 现代扁平风格应用图标（256x256）并直接打包 ICO。
// 设计语言：圆角方形靛蓝→蓝渐变底 + 白色挂锁 + 蓝色锁孔，与 UI 主色 #2563EB 呼应。
// 输出:
//   <out.ico>          PNG-in-ICO 单图层 256（Vista+ 通用，与旧 app.ico 同机制）
//   <out.png>          256 预览图（供人工检查）
// 用法: IconMaker <out.ico> <out.png>

const int S = 256;

using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(bmp))
{
    g.SmoothingMode = SmoothingMode.AntiAlias;

    // ---- 背景：对角渐变圆角方形（#3B82F6 → #1E40AF），半径 58 视觉居中 ----
    var bgPath = RoundedRect(8, 8, 240, 240, 58);
    using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, S, S),
        Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x1E, 0x40, 0xAF), 55F))
        g.FillPath(bgBrush, bgPath);

    // 顶部柔光：半透明白椭圆压在左上角，增加玻璃质感
    // 椭圆下沿(±110)与渐变归零高度(110)对齐 → 接缝处 alpha=0，不会留硬边
    using (var glow = new GraphicsPath())
    {
        glow.AddEllipse(-70, -100, 280, 210);
        using var glowBrush = new LinearGradientBrush(new Rectangle(0, 0, S, 110),
            Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90F);
        g.SetClip(bgPath);
        g.FillPath(glowBrush, glow);
        g.ResetClip();
    }

    // ---- 挂锁锁梁：白色粗描边半圆拱 + 两根竖腿 ----
    using (var pen = new Pen(Color.White, 20f)
    { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
    {
        var arc = new Rectangle(90, 46, 76, 76);     // 圆心(128,84) 半径38
        g.DrawArc(pen, arc, 180f, 180f);             // 上半圆
        g.DrawLine(pen, 90, 84, 90, 122);            // 左腿
        g.DrawLine(pen, 166, 84, 166, 122);          // 右腿
    }

    // ---- 锁体：白色圆角矩形（含一点底部柔和阴影）----
    var body = RoundedRect(62, 112, 132, 100, 22);
    using (var shadow = new GraphicsPath())
    {
        shadow.AddEllipse(58, 196, 140, 26);
        using var shadowBrush = new SolidBrush(Color.FromArgb(36, 0, 30, 90));
        g.FillPath(shadowBrush, shadow);
    }
    g.FillPath(Brushes.White, body);

    // ---- 锁孔：蓝色圆 + 竖条（用背景同款渐变，视觉上"透出底色"）----
    using (var holeBrush = new LinearGradientBrush(new Rectangle(96, 130, 64, 70),
        Color.FromArgb(0x25, 0x63, 0xEB), Color.FromArgb(0x1E, 0x40, 0xAF), 55F))
    {
        g.FillEllipse(holeBrush, 114, 138, 28, 28);            // 锁孔圆
        var stem = RoundedRect(121, 158, 14, 30, 7);           // 锁孔竖条
        g.FillPath(holeBrush, stem);
    }
}

// ---- 存 PNG 预览 ----
bmp.Save(args.Length > 1 ? args[1] : "icon.png", ImageFormat.Png);

// ---- 打包 PNG-in-ICO（ICONDIR + ICONDIRENTRY + PNG 数据，与 IcoMaker 同格式）----
byte[] png;
using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); png = ms.ToArray(); }

string icoPath = args.Length > 0 ? args[0] : "app.ico";
using (var fs = File.Create(icoPath))
using (var bw = new BinaryWriter(fs))
{
    bw.Write((ushort)0);   // reserved
    bw.Write((ushort)1);   // type = icon
    bw.Write((ushort)1);   // count = 1
    bw.Write((byte)0);     // width  = 256
    bw.Write((byte)0);     // height = 256
    bw.Write((byte)0);     // colors
    bw.Write((byte)0);     // reserved
    bw.Write((ushort)1);   // planes
    bw.Write((ushort)32);  // bit count
    bw.Write((uint)png.Length);
    bw.Write((uint)22);    // data offset
    bw.Write(png);
}
Console.WriteLine($"OK {icoPath} ({png.Length} bytes png) + preview png");
return 0;

static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
{
    var p = new GraphicsPath();
    int d = r * 2;
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}
