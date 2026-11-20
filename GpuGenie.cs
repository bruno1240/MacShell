using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MacShell;

/// <summary>Foto da janela (BGRA) usada como textura do efeito gênio.</summary>
internal sealed record WindowSnapshot(byte[] Pixels, int Width, int Height, float CornerRadius);

/// <summary>
/// Efeito gênio renderizado na placa de vídeo (Direct3D 11 + DirectComposition):
/// a foto da janela vira uma textura com mipmaps e filtro anisotrópico, aplicada numa malha
/// que se deforma a cada quadro. Bordas e cantos suavizados no shader, sincronizado com o monitor
/// (Present com vsync) e desenhado direto pelo compositor — sem cópias pela CPU.
/// </summary>
internal sealed class GpuGenie
{
    const int Rows = 120;
    const double MinimizeSeconds = 0.46;
    const double RestoreSeconds = 0.40;

    readonly Dispatcher _ui;
    readonly BlockingCollection<Action> _jobs = new();
    int _generation;

    IntPtr _hwnd;
    int _screenW, _screenH;
    public bool Ready { get; private set; }
    public string? Error { get; private set; }

    // D3D
    ID3D11Device? _device;
    ID3D11DeviceContext? _ctx;
    IDXGISwapChain1? _swap;
    ID3D11RenderTargetView? _rtv;
    ID3D11VertexShader? _vs;
    ID3D11PixelShader? _ps;
    ID3D11InputLayout? _layout;
    ID3D11Buffer? _vb, _cb;
    ID3D11SamplerState? _sampler;
    ID3D11BlendState? _blend;
    ID3D11RasterizerState? _raster;
    IDCompositionDevice? _dcomp;
    IDCompositionTarget? _target;
    IDCompositionVisual? _visual;
    ID3D11Texture2D? _tex;
    ID3D11ShaderResourceView? _srv;

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex { public Vector2 Pos; public Vector2 Uv; }

    [StructLayout(LayoutKind.Sequential)]
    struct Constants { public Vector2 Screen; public Vector2 TexSize; public float Radius; public float Opacity; public Vector2 Pad; }

    public GpuGenie(Dispatcher ui)
    {
        _ui = ui;
        _screenW = Native.GetSystemMetrics(0);
        _screenH = Native.GetSystemMetrics(1);
        _hwnd = CreateOverlayWindow(_screenW, _screenH); // na thread da interface (ela processa as mensagens)
        var t = new Thread(Worker) { IsBackground = true, Name = "MacShell-gpu", Priority = ThreadPriority.Highest };
        t.Start();
        _jobs.Add(Init);
    }

    void Worker()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception ex) { Error = ex.Message; Log(ex.ToString()); }
        }
    }

    // ------------------------------------------------------------------ inicialização
    void Init()
    {
        try
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                out _device, out _ctx).CheckError();

            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            var desc = new SwapChainDescription1
            {
                Width = 8,
                Height = 8,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
            };
            _swap = factory.CreateSwapChainForComposition(_device, desc, null);
            using (var back = _swap.GetBuffer<ID3D11Texture2D>(0))
                _rtv = _device.CreateRenderTargetView(back);

            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _dcomp.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
            _dcomp.CreateVisual(out _visual).CheckError();
            _visual!.SetContent(_swap);
            _target!.SetRoot(_visual);
            _dcomp.Commit();

            var vsCode = Compiler.Compile(Shader, "VS", "genie.hlsl", "vs_5_0");
            var psCode = Compiler.Compile(Shader, "PS", "genie.hlsl", "ps_5_0");
            _vs = _device.CreateVertexShader(vsCode.Span);
            _ps = _device.CreatePixelShader(psCode.Span);
            _layout = _device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            }, vsCode.Span);

            _vb = _device.CreateBuffer(new BufferDescription((uint)(Marshal.SizeOf<Vertex>() * (Rows + 1) * 2),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _cb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(),
                BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxAnisotropy = 16,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });
            _blend = _device.CreateBlendState(BlendDescription.AlphaBlend); // alfa pré-multiplicado
            _raster = _device.CreateRasterizerState(RasterizerDescription.CullNone);

            ClearAndPresent();
            Ready = true;
        }
        catch (Exception ex)
        {
            Ready = false;
            Error = ex.Message;
            Log("GPU genie indisponível: " + ex);
        }
    }

    // ------------------------------------------------------------------ animação
    /// <param name="firstFrame">roda na interface quando o 1º quadro já está na tela</param>
    /// <param name="done">roda na interface no fim</param>
    public void Play(WindowSnapshot snap, Native.RECT windowRect, Native.RECT iconRect, bool restore,
                     Action? firstFrame = null, Action? done = null)
    {
        int gen = Interlocked.Increment(ref _generation);
        _jobs.Add(() => Run(gen, snap, windowRect, iconRect, restore, firstFrame, done));
    }

    void Run(int gen, WindowSnapshot snap, Native.RECT from, Native.RECT to, bool restore, Action? firstFrame, Action? done)
    {
        if (!Ready || _ctx == null) { if (firstFrame != null) _ui.BeginInvoke(firstFrame); if (done != null) _ui.BeginInvoke(done); return; }
        Resize(_screenW, _screenH);
        UploadTexture(snap);

        var cs = new Constants
        {
            Screen = new Vector2(_screenW, _screenH),
            TexSize = new Vector2(snap.Width, snap.Height),
            Radius = snap.CornerRadius,
            Opacity = 1,
        };

        Native.SetWindowPos(_hwnd, new IntPtr(-1) /* TOPMOST */, 0, 0, 0, 0,
            0x1 | 0x2 | 0x10 | 0x40 | Native.SWP_ASYNCWINDOWPOS); // NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW

        var verts = new Vertex[(Rows + 1) * 2];
        double duration = restore ? RestoreSeconds : MinimizeSeconds;
        var sw = Stopwatch.StartNew();
        bool first = true;
        int frames = 0;
        double maxGap = 0, last = 0;

        while (true)
        {
            bool cancelled = gen != Volatile.Read(ref _generation);
            double t = Math.Min(1, sw.Elapsed.TotalSeconds / duration);
            if (cancelled) t = 1;
            double eased = restore ? 1 - Math.Pow(1 - t, 2.4) : 0.45 * t + 0.55 * t * t;
            double p = restore ? 1 - eased : eased;

            cs.Opacity = (float)(1 - 0.45 * Clamp01((p - 0.86) / 0.14));
            BuildMesh(verts, p, from, to);
            Draw(verts, cs);
            _swap!.Present(1, PresentFlags.None); // espera o próximo quadro do monitor
            frames++;
            double now = sw.Elapsed.TotalMilliseconds;
            if (frames > 1) maxGap = Math.Max(maxGap, now - last);
            last = now;

            if (first) { first = false; if (firstFrame != null) _ui.BeginInvoke(firstFrame); }
            if (t >= 1) break;
        }

        if (restore && done != null)
        {
            _ui.BeginInvoke(done);   // a janela real aparece
            Thread.Sleep(70);        // segura o último quadro até ela ser desenhada (sem piscar)
        }
        else if (done != null) _ui.BeginInvoke(done);

        if (gen == Volatile.Read(ref _generation))
        {
            ClearAndPresent();
            Native.ShowWindowAsync(_hwnd, Native.SW_HIDE);
            // devolve a memória de vídeo entre uma animação e outra
            _srv?.Dispose(); _srv = null;
            _tex?.Dispose(); _tex = null;
            Resize(8, 8);
            _ctx.ClearState();
            _ctx.Flush();
        }
        Log($"gpu {(restore ? "restaurar" : "minimizar")} {snap.Width}x{snap.Height} quadros={frames} em {last:F0}ms maiorIntervalo={maxGap:F1}ms");
    }

    void UploadTexture(WindowSnapshot snap)
    {
        _srv?.Dispose(); _tex?.Dispose();
        _tex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)snap.Width,
            Height = (uint)snap.Height,
            MipLevels = 0, // cadeia completa: fica nítido mesmo bem espremido
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        var handle = GCHandle.Alloc(snap.Pixels, GCHandleType.Pinned);
        try { _ctx!.UpdateSubresource(_tex, 0, null, handle.AddrOfPinnedObject(), (uint)(snap.Width * 4), 0); }
        finally { handle.Free(); }
        _srv = _device.CreateShaderResourceView(_tex);
        _ctx!.GenerateMips(_srv);
    }

    /// <summary>
    /// Curva do gênio. p = 0: janela inteira; p = 1: dentro do ícone.
    /// A base desce e afina primeiro; o topo é "sugado" depois, comprimindo o conteúdo pelo funil.
    /// </summary>
    static void BuildMesh(Vertex[] v, double p, Native.RECT from, Native.RECT to)
    {
        double rl = from.Left, rr = from.Right, rt = from.Top, rb = from.Bottom;
        double tl = to.Left, tr = to.Right, tt = to.Top, tb = to.Bottom;

        double bottom = Lerp(rb, tb, Smooth(Clamp01(p / 0.58)));
        double topT = Clamp01((p - 0.18) / 0.82);
        double top = Lerp(rt, tt, topT * topT * (3 - 2 * topT) * 0.3 + topT * topT * topT * 0.7);
        double pinch = Smooth(Clamp01(p / 0.42));
        double collapse = Clamp01((p - 0.18) / 0.82);
        collapse = collapse * collapse * collapse;
        double span = Math.Max(1, tb - rt);

        for (int i = 0; i <= Rows; i++)
        {
            float vv = (float)i / Rows;
            double y = Lerp(top, bottom, vv);
            double f = Clamp01((y - rt) / span);
            double k = Math.Max(pinch * Smooth(Smooth(f)), collapse);
            double left = Lerp(rl, tl, k), right = Lerp(rr, tr, k);
            v[i * 2] = new Vertex { Pos = new Vector2((float)left, (float)y), Uv = new Vector2(0, vv) };
            v[i * 2 + 1] = new Vertex { Pos = new Vector2((float)right, (float)y), Uv = new Vector2(1, vv) };
        }
    }

    void Draw(Vertex[] verts, Constants cs)
    {
        var ctx = _ctx!;
        var mapped = ctx.Map(_vb!, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            fixed (Vertex* src = verts)
                Buffer.MemoryCopy(src, (void*)mapped.DataPointer, verts.Length * sizeof(Vertex), verts.Length * sizeof(Vertex));
        }
        ctx.Unmap(_vb!, 0);
        var mc = ctx.Map(_cb!, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(cs, mc.DataPointer, false);
        ctx.Unmap(_cb!, 0);

        ctx.OMSetRenderTargets(_rtv!);
        ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 0));
        ctx.RSSetViewport(0, 0, _screenW, _screenH);
        ctx.RSSetState(_raster);
        ctx.OMSetBlendState(_blend);
        ctx.IASetInputLayout(_layout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetVertexBuffer(0, _vb!, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.PSSetShaderResource(0, _srv!);
        ctx.PSSetSampler(0, _sampler);
        ctx.Draw((uint)verts.Length, 0);
    }

    uint _bufW = 8, _bufH = 8;
    void Resize(int w, int h)
    {
        if (_bufW == (uint)w && _bufH == (uint)h) return;
        _ctx!.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _rtv?.Dispose();
        _swap!.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        using (var back = _swap.GetBuffer<ID3D11Texture2D>(0))
            _rtv = _device!.CreateRenderTargetView(back);
        _bufW = (uint)w; _bufH = (uint)h;
    }

    void ClearAndPresent()
    {
        _ctx!.OMSetRenderTargets(_rtv!);
        _ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 0));
        _swap!.Present(0, PresentFlags.None);
    }

    const string Shader = @"
cbuffer C : register(b0) { float2 screen; float2 texSize; float radius; float opacity; float2 pad; };
struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct PSIn { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
PSIn VS(VSIn i)
{
    PSIn o;
    o.pos = float4(i.pos.x / screen.x * 2 - 1, 1 - i.pos.y / screen.y * 2, 0, 1);
    o.uv = i.uv;
    return o;
}
Texture2D tex : register(t0);
SamplerState smp : register(s0);
float4 PS(PSIn i) : SV_TARGET
{
    float3 rgb = tex.Sample(smp, i.uv).rgb;

    // cantos arredondados (no espaço da janela original), com borda suave
    float2 p = i.uv * texSize;
    float2 q = min(p, texSize - p);
    float2 d = max(radius - q, 0);
    float dist = length(d) - radius;
    float w = max(fwidth(dist), 0.0001);
    float corner = radius > 0 ? saturate(0.5 - dist / w) : 1;

    // anti-serrilhado nas bordas da malha (em pixels de tela)
    float e = min(min(i.uv.x, 1 - i.uv.x), min(i.uv.y, 1 - i.uv.y));
    float edge = saturate(e / max(fwidth(e), 0.00001) + 0.25);

    float a = corner * edge * opacity;
    return float4(rgb * a, a);
}";

    // ------------------------------------------------------------------ janela sem bitmap (só DirectComposition)
    static Native.WndProc? _wndProc;

    static IntPtr CreateOverlayWindow(int w, int h)
    {
        _wndProc = (hwnd, msg, wp, lp) => msg == 0x0084 /* WM_NCHITTEST */ ? new IntPtr(-1) /* HTTRANSPARENT */ : Native.DefWindowProc(hwnd, msg, wp, lp);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "MacShellGpuGenie",
        };
        Native.RegisterClassEx(ref wc);
        const uint WS_POPUP = 0x80000000;
        const uint ex = 0x00200000 /* NOREDIRECTIONBITMAP */ | 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */;
        return Native.CreateWindowEx(ex, "MacShellGpuGenie", "MacShell GPU Genie", WS_POPUP, 0, 0, w, h,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ util
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    static double Smooth(double x) => x * x * (3 - 2 * x);

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(DockConfig.Dir, "anim.log"), $"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}"); } catch { }
    }
}
