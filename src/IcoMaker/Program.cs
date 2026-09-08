using System;
using System.IO;

// 一次性工具：把 PNG 直接打包成 ICO（PNG-in-ICO 单图层，Vista+ 通用）。
// 源 PNG 应为 256x256（与 ICO 单图层最大尺寸匹配），不做任何缩放，零依赖。
// 用法: IcoMaker <src.png> <out.ico>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: IcoMaker <src.png> <out.ico>");
            return 2;
        }
        byte[] png = File.ReadAllBytes(args[0]);

        // 从 PNG IHDR 读宽高（用于 ICO 头 width/height 字段；>255 时填 0=256）
        int w = 256, h = 256;
        if (png.Length >= 24 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47)
        {
            w = ReadInt32BE(png, 16);
            h = ReadInt32BE(png, 20);
        }

        using var fs = File.Create(args[1]);
        using var bw = new BinaryWriter(fs);
        // ICONDIR (6 bytes)
        bw.Write((ushort)0);     // reserved
        bw.Write((ushort)1);     // type = 1 (icon)
        bw.Write((ushort)1);     // count = 1
        // ICONDIRENTRY (16 bytes) —— width/height 字段：0 表示 256，>255 也写 0
        byte wh = (byte)((w >= 256 && h >= 256) ? 0 : Math.Max(w, h));
        bw.Write(wh);
        bw.Write(wh);
        bw.Write((byte)0);       // color count
        bw.Write((byte)0);       // reserved
        bw.Write((ushort)1);     // planes
        bw.Write((ushort)32);    // bit count
        bw.Write((uint)png.Length);
        bw.Write((uint)22);      // offset to image data
        // PNG data
        bw.Write(png);
        Console.WriteLine($"OK {args[1]} ({w}x{h}, PNG {png.Length} bytes)");
        return 0;
    }

    static int ReadInt32BE(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }
}
