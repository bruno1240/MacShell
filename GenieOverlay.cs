using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacShell;

/// <summary>
/// Efeito "gênio" do macOS: a janela escorre para dentro do ícone do dock (e sai dele ao restaurar).
/// A janela é fatiada em faixas horizontais finas; cada faixa é uma miniatura ao vivo do DWM
/// (desenhada pela GPU), reposicionada a cada quadro do monitor numa thread própria.
/// </summary>
internal sealed class GenieOverlay : Window
{
    const int MinSlices = 60, MaxSlices = 110;
    const double MinimizeSeconds = 0.45;
    const double RestoreSeconds = 0.40;

    IntPtr _hwnd;
    IntPtr[] _thumbs = Array.Empty<IntPtr>();
    readonly FrameLoop _loop = new();
    Native.RECT _from, _to;
    int _srcW, _srcH;
    bool _restore, _active;
    Action? _done;

    public GenieOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "MacShell Genie";
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd, noActivate: true);
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_TRANSPARENT; // clique atravessa
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Cria a camada uma vez (transparente) e a deixa escondida, pronta para uso imediato.</summary>
    public void Prepare()
    {
        double dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this).DpiScaleX;
        Left = 0; Top = 0;
        Width = Native.GetSystemMetrics(0) / dpi;
        Height = Native.GetSystemMetrics(1) / dpi;
        Show();
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
    }

    IntPtr _still;

    /// <summary>
    /// Fotografa a janela (mesmo já minimizada, o DWM guarda a imagem): desenha a miniatura dela
    /// no lugar original, espera o compositor e copia a tela. A miniatura fica visível até
    /// <see cref="HideStill"/> — sem "piscar" enquanto a GPU assume.
    /// </summary>
    public WindowSnapshot? CaptureStill(IntPtr window, Native.RECT rect, float cornerRadius)
    {
        Finish();
        HideStill();
        if (_hwnd == IntPtr.Zero) return null;
        Native.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40); // TOPMOST, mostra sem ativar
        if (Native.DwmRegisterThumbnail(_hwnd, window, out _still) != 0) { _still = IntPtr.Zero; return null; }
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_OPACITY | Native.DWM_TNP_VISIBLE,
            rcDestination = rect,
            opacity = 255,
            fVisible = 1,
        };
        Native.DwmUpdateThumbnailProperties(_still, ref props);
        Native.DwmFlush();
        Native.DwmFlush();
        var px = Native.CaptureScreen(rect, out int w, out int h);
        return px == null ? null : new WindowSnapshot(px, w, h, cornerRadius);
    }

    public void HideStill()
    {
        if (_still != IntPtr.Zero) { Native.DwmUnregisterThumbnail(_still); _still = IntPtr.Zero; }
        if (!_active && _hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_HIDE);
    }

    public void Play(IntPtr window, Native.RECT windowRect, Native.RECT iconRect, bool restore, Action? done = null)
    {
        Finish(); // termina qualquer animação anterior na hora

        int h = windowRect.Bottom - windowRect.Top;
        if (windowRect.Right - windowRect.Left < 40 || h < 40 || _hwnd == IntPtr.Zero) { done?.Invoke(); return; }

        var diag = System.Diagnostics.Stopwatch.StartNew();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNA);
        double tShow = diag.Elapsed.TotalMilliseconds;
        _from = windowRect; _to = iconRect; _restore = restore; _done = done;

        int slices = Math.Clamp(h / 4, MinSlices, MaxSlices);
        var thumbs = new IntPtr[slices];
        for (int i = 0; i < slices; i++)
        {
            if (Native.DwmRegisterThumbnail(_hwnd, window, out thumbs[i]) != 0)
            {
                for (int j = 0; j < i; j++) Native.DwmUnregisterThumbnail(thumbs[j]);
                Native.ShowWindow(_hwnd, Native.SW_HIDE);
                _done = null;
                done?.Invoke();
                return;
            }
        }
        _thumbs = thumbs;
        Native.DwmQueryThumbnailSourceSize(thumbs[0], out var size);
        _srcW = size.cx > 0 ? size.cx : windowRect.Right - windowRect.Left;
        _srcH = size.cy > 0 ? size.cy : h;

        double tReg = diag.Elapsed.TotalMilliseconds;
        Apply(thumbs, restore ? 1 : 0);
        double tFirst = diag.Elapsed.TotalMilliseconds;
        _active = true;
        int frames = 0; double lastMs = 0, maxGap = 0, applyMax = 0;
        var frameSw = System.Diagnostics.Stopwatch.StartNew();
        _loop.Run(restore ? RestoreSeconds : MinimizeSeconds, t =>
        {
            double now = frameSw.Elapsed.TotalMilliseconds;
            if (frames > 0) maxGap = Math.Max(maxGap, now - lastMs);
            lastMs = now; frames++;
            // Começa a se mexer já no 1º quadro (sem "arrancada" lenta) e assenta suave no final.
            double eased = restore ? 1 - Math.Pow(1 - t, 2.2) : 0.5 * t + 0.5 * t * t;
            Apply(thumbs, restore ? 1 - eased : eased);
            applyMax = Math.Max(applyMax, frameSw.Elapsed.TotalMilliseconds - now);
        }, Dispatcher, () =>
        {
            try
            {
                File.AppendAllText(Path.Combine(DockConfig.Dir, "anim.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {(restore ? "restaurar" : "minimizar")} faixas={slices} mostrar={tShow:F1}ms registrar={tReg - tShow:F1}ms 1ºquadro={tFirst - tReg:F1}ms quadros={frames} em {lastMs:F0}ms maiorIntervalo={maxGap:F1}ms maiorApply={applyMax:F1}ms" + Environment.NewLine);
            }
            catch { }
            Finish();
        });
    }

    /// <summary>p = 0: janela inteira no lugar; p = 1: dentro do ícone.</summary>
    void Apply(IntPtr[] thumbs, double p)
    {
        int n = thumbs.Length;
        double rl = _from.Left, rr = _from.Right, rt = _from.Top, rb = _from.Bottom;
        double tl = _to.Left, tr = _to.Right, tt = _to.Top, tb = _to.Bottom;

        // A base desce primeiro (e afina); o topo vem depois, "sugado" para o ícone.
        double bottom = Lerp(rb, tb, Smooth(Clamp01(p / 0.6)));
        double topT = Clamp01((p - 0.2) / 0.8);
        double top = Lerp(rt, tt, topT * topT * (3 - 2 * topT) * 0.35 + topT * topT * topT * 0.65);
        double pinch = Smooth(Clamp01(p / 0.45));
        double collapse = Clamp01((p - 0.2) / 0.8);
        collapse = collapse * collapse * collapse;
        double span = Math.Max(1, tb - rt);
        byte opacity = (byte)(255 * (1 - 0.4 * Clamp01((p - 0.85) / 0.15)));

        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_VISIBLE,
            opacity = opacity,
        };

        for (int i = 0; i < n; i++)
        {
            double v0 = (double)i / n, v1 = (double)(i + 1) / n;
            double y0 = Lerp(top, bottom, v0), y1 = Lerp(top, bottom, v1);
            double yc = (y0 + y1) / 2;

            double f = Clamp01((yc - rt) / span);
            double k = Math.Max(pinch * Smooth(Smooth(f)), collapse);
            double left = Lerp(rl, tl, k), right = Lerp(rr, tr, k);

            int dTop = (int)Math.Round(y0), dBottom = (int)Math.Round(y1);
            if (dBottom <= dTop) dBottom = dTop + 1; // sem buracos entre faixas
            props.rcSource = new Native.RECT { Left = 0, Right = _srcW, Top = (int)(_srcH * v0), Bottom = Math.Max((int)(_srcH * v0) + 1, (int)Math.Round(_srcH * v1)) };
            props.rcDestination = new Native.RECT { Left = (int)Math.Round(left), Right = (int)Math.Round(right), Top = dTop, Bottom = dBottom };
            props.fVisible = right - left >= 1 && y1 - y0 > 0.02 ? 1 : 0;
            Native.DwmUpdateThumbnailProperties(thumbs[i], ref props);
        }
    }

    void Finish()
    {
        if (!_active && _thumbs.Length == 0) return;
        _loop.Cancel();
        _active = false;
        var thumbs = _thumbs;
        _thumbs = Array.Empty<IntPtr>();
        bool restore = _restore;
        var done = _done;
        _done = null;
        done?.Invoke(); // ao restaurar: a janela real aparece aqui

        // Ao restaurar, segura as faixas ~3 quadros até a janela real ser desenhada (sem piscar).
        Dispatcher.BeginInvoke(async () =>
        {
            if (restore) await Task.Delay(50);
            foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t);
            if (!_active) Native.ShowWindow(_hwnd, Native.SW_HIDE); // nada sobrando sobre jogos/janelas
        });
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    static double Smooth(double x) => x * x * (3 - 2 * x);
}
