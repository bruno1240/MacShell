using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacShell;

/// <summary>
/// Fundo "vidro" da barra superior, do jeito que o macOS faz na prática: atrás da barra só existe o
/// papel de parede (as janelas nunca passam por baixo dela), então basta desfocar essa faixa do
/// papel de parede uma vez. Fica idêntico a um vidro fosco e não custa nada enquanto roda.
/// </summary>
internal static class WallpaperBackdrop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buf, uint flags);

    /// <param name="widthDip">largura da barra</param>
    /// <param name="heightDip">altura da barra</param>
    public static Brush? Build(double widthDip, double heightDip, bool light)
    {
        try
        {
            string? path = WallpaperPath();
            if (path == null || !File.Exists(path)) return null;

            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;

            // Lê em resolução reduzida: vai ser desfocado de qualquer jeito (leve na memória).
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = (int)Math.Max(320, sw / 2);
            img.EndInit();
            img.Freeze();

            Rect drawn = PlaceLikeWindows(img.PixelWidth, img.PixelHeight, sw, sh);

            // Desenha a faixa de cima (com margem para o desfoque não "escurecer" a borda) e desfoca.
            const double margin = 40, scale = 0.5;
            double bandH = heightDip + margin;
            var visual = new DrawingVisual { Effect = new BlurEffect { Radius = 26, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new SolidColorBrush(AverageTopColor(img)), null, new Rect(-margin, -margin, widthDip + 2 * margin, bandH + 2 * margin));
                dc.DrawImage(img, new Rect(drawn.X, drawn.Y - 0, drawn.Width, drawn.Height));
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(bandH * scale), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            // Vidro: o fundo desfocado + um véu leve (escuro no tema escuro, claro no claro).
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var area = new Rect(0, 0, widthDip, heightDip);
                dc.PushClip(new RectangleGeometry(area));
                dc.DrawImage(rtb, new Rect(0, 0, widthDip, bandH));
                dc.DrawRectangle(new SolidColorBrush(light ? Color.FromArgb(0x59, 0xF6, 0xF6, 0xF6) : Color.FromArgb(0x47, 0x10, 0x10, 0x10)), null, area);
                dc.Pop();
            }
            group.Freeze();
            var brush = new DrawingBrush(group) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            brush.Freeze();
            return brush;
        }
        catch { return null; }
    }

    static string? WallpaperPath()
    {
        var sb = new StringBuilder(520);
        if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0) && sb.Length > 0) return sb.ToString();
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return k?.GetValue("WallPaper") as string;
    }

    /// <summary>Onde o Windows desenha o papel de parede, conforme o estilo escolhido (Preencher, Ajustar, Estender...).</summary>
    static Rect PlaceLikeWindows(double iw, double ih, double sw, double sh)
    {
        string style = "10";
        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            style = k?.GetValue("WallpaperStyle") as string ?? "10";
        double s;
        switch (style)
        {
            case "2": return new Rect(0, 0, sw, sh);                            // Estender
            case "6": s = Math.Min(sw / iw, sh / ih); break;                    // Ajustar
            case "0": s = 1; break;                                             // Centralizar
            default: s = Math.Max(sw / iw, sh / ih); break;                     // Preencher / Expandir
        }
        double w = iw * s, h = ih * s;
        return new Rect((sw - w) / 2, (sh - h) / 2, w, h);
    }

    static Color AverageTopColor(BitmapSource img)
    {
        int w = img.PixelWidth, h = Math.Max(1, img.PixelHeight / 10);
        var conv = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var px = new byte[w * h * 4];
        conv.CopyPixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        long b = 0, g = 0, r = 0, n = 0;
        for (int i = 0; i < px.Length; i += 16) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
        return n == 0 ? Colors.Gray : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
