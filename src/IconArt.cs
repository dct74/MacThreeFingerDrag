namespace Mftd;

/// <summary>应用元信息（集中在一处，托盘「关于」与版本号都用它）。</summary>
internal static class AppInfo
{
    public const string Name = "MacThreeFingerDrag";
    public const string Version = "1.0.0";
    public const string RepoUrl = "https://github.com/dct74/MacThreeFingerDrag";
}

/// <summary>
/// 图标：运行时矢量绘制（托盘 HICON）以及构建期写 .ico（exe 资源）。
/// 同一个绘制函数保证托盘图标与 exe 图标完全一致。
/// </summary>
internal static class IconArt
{
    /// <summary>返回 size x size 的像素，格式 0xAARRGGBB（与 DIB 内存顺序一致：BGRA 小端）。</summary>
    public static uint[] Render(int size)
    {
        var px = new uint[size * size];
        const double s = 32.0;
        double k = size / s;
        const int R = 0x1F, G = 0x6F, B = 0xEB; // 圆角蓝底

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double covBg = 0, covBar = 0;
                for (int sy = 0; sy < 3; sy++)
                {
                    for (int sx = 0; sx < 3; sx++)
                    {
                        double px32 = (x + (sx + 0.5) / 3.0) / k;
                        double py32 = (y + (sy + 0.5) / 3.0) / k;
                        if (InRoundRect(px32, py32, 1.5, 1.5, 29, 29, 6)) covBg += 1;
                        // 三根手指（中间那根略高，像搭在板上）
                        if (px32 >= 9.5 && px32 <= 13 && py32 >= 7 && py32 <= 23) covBar += 1;
                        else if (px32 >= 14.5 && px32 <= 18 && py32 >= 6 && py32 <= 23) covBar += 1;
                        else if (px32 >= 19.5 && px32 <= 23 && py32 >= 7 && py32 <= 23) covBar += 1;
                    }
                }
                double ab = covBg / 9.0, ar = covBar / 9.0;
                if (ab <= 0 && ar <= 0) { px[y * size + x] = 0; continue; }

                double r = R, g = G, b = B;
                if (ar > 0)
                {
                    double t = Math.Min(1.0, ar / Math.Max(ab, 1e-6));
                    r = r * (1 - t) + 255 * t;
                    g = g * (1 - t) + 255 * t;
                    b = b * (1 - t) + 255 * t;
                }
                byte a = (byte)(Math.Clamp(ab, 0, 1) * 255);
                px[y * size + x] = (uint)(a << 24 | (byte)r << 16 | (byte)g << 8 | (byte)b);
            }
        }
        return px;
    }

    private static bool InRoundRect(double x, double y, double left, double top, double right, double bottom, double radius)
    {
        double cx = Math.Clamp(x, left + radius, right - radius);
        double cy = Math.Clamp(y, top + radius, bottom - radius);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>写多尺寸 .ico（32bpp BGRA + 全零 AND 掩码）。用于构建期生成 exe 图标资源。</summary>
    public static void WriteIco(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            var px = Render(size);
            int xorSize = size * size * 4;
            int andRow = ((size + 31) / 32) * 4;
            int andSize = andRow * size;
            var img = new byte[40 + xorSize + andSize];
            // BITMAPINFOHEADER：ICO 里高度要写两倍（XOR + AND）
            void W(int off, int v) { img[off] = (byte)v; img[off + 1] = (byte)(v >> 8); img[off + 2] = (byte)(v >> 16); img[off + 3] = (byte)(v >> 24); }
            W(0, 40); W(4, size); W(8, size * 2);
            img[12] = 1; img[14] = 32;                     // biPlanes=1, biBitCount=32
            W(20, xorSize + andSize);                      // biSizeImage
            for (int y = 0; y < size; y++)                 // DIB 是自底向上
            {
                var src = (size - 1 - y) * size;
                for (int x = 0; x < size; x++)
                {
                    uint c = px[src + x];
                    int o = 40 + (y * size + x) * 4;
                    img[o] = (byte)(c & 0xFF);             // B
                    img[o + 1] = (byte)((c >> 8) & 0xFF);  // G
                    img[o + 2] = (byte)((c >> 16) & 0xFF); // R
                    img[o + 3] = (byte)((c >> 24) & 0xFF);// A
                }
            }
            images.Add(img); // AND 掩码保持全 0（不透明）
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int size = sizes[i];
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }
}
