using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacShell;

public partial class DockWindow : Window
{
    // ------------------------------------------------------------ medidas (em DIPs)
    const double IconSize = 50;      // tamanho do ícone em repouso
    const double Gap = 7;            // espaço entre ícones
    const double MaxScale = 1.8;     // ampliação máxima sob o mouse
    const double Sigma = 62;         // alcance do efeito de lupa (px)
    const double PadX = 7;
    const double PadTop = 6;
    const double DotArea = 8;
    const double PadBottom = 3;
    const double PillBottom = 4;
    const double SepWidth = 15;
    const double PillHeight = PadTop + IconSize + DotArea + PadBottom;
    const double WinHeight = 160;
    const double Reserve = PillBottom + PillHeight + 4;

    const string SepKey = "|sep", TrashKey = "|trash";
    const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    IntPtr _hwnd;
    List<PinnedApp> _pinned = DockConfig.Load();
    readonly Dictionary<string, DockItem> _views = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _runningOrder = new();
    readonly Dictionary<uint, string?> _pathCache = new();
    int _refreshCount;
    List<string> _currentKeys = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    // pedidos de atenção (botões piscando, pedido de administrador)
    readonly HashSet<IntPtr> _attention = new();
    bool _uacPending;
    ShellHookWindow? _shellWindow;

    IntPtr _hook, _minHook, _locHook;
    Native.WinEventDelegate? _hookProc, _minHookProc, _locHookProc;
    uint _locPid;

    // gênio + esconder automático
    readonly GenieOverlay _genie = new();
    GpuGenie? _gpu;
    readonly Dictionary<IntPtr, Native.RECT> _lastRect = new();
    readonly HashSet<IntPtr> _tuned = new();
    Window? _edge; // sensor invisível de 2 px na borda de baixo: o mouse encosta e o dock sobe (sem nenhum timer)
    readonly DispatcherTimer _hideDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly FrameLoop _slide = new();
    bool _dockHidden, _fullscreen, _evalQueued;
    double _shownTop, _hiddenTop;
    double _curYpx = double.NaN;
    int _evalCount;

    /// <summary>Um app entrou/saiu de tela cheia de verdade (jogo, vídeo, F11).</summary>
    public event Action<bool>? FullscreenChanged;

    bool _hovering, _animating;

    // arrastar e soltar para fixar
    bool _dragging;
    double _dragX;
    int _dropIndex;
    DockItem? _placeholder;
    readonly DispatcherTimer _dragLeave = new() { Interval = TimeSpan.FromMilliseconds(90) };

    // reordenar arrastando dentro do dock
    DockItem? _pressItem, _reorderItem;
    Point _pressPos, _ghostOffset;
    bool _reordering;
    Image? _ghost;
    DockItem? _hoverItem;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFrame;
    readonly Border _edgeStrip = new() { Background = Theme.Brush(0x01000000), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Height = PillBottom };

    public DockWindow()
    {
        InitializeComponent();
        Height = WinHeight;
        Root.Children.Insert(0, _edgeStrip);
        Pill.Height = PillHeight;
        Pill.Margin = new Thickness(0, 0, 0, PillBottom);
        PillHighlight.Height = PillHeight - 2;
        PillHighlight.Margin = new Thickness(0, 0, 0, PillBottom + 1);
        Items.Margin = new Thickness(0, 0, 0, PillBottom + PadBottom);

        SourceInitialized += OnSourceInitialized;
        Root.MouseEnter += (_, _) => { _hovering = true; StartAnimation(); };
        Root.MouseMove += (_, e) => OnRootMouseMove(e);
        Root.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _pressItem = null;
            if (_reordering) { EndReorder(true); e.Handled = true; } // não conta como clique
        };
        Root.LostMouseCapture += (_, _) => { if (_reordering) EndReorder(true); };
        Root.MouseLeave += (_, _) => { _hovering = false; _hoverItem = null; StartAnimation(); _hideDelay.Stop(); _hideDelay.Start(); };
        Pill.MouseRightButtonUp += (_, e) => { ShowDockMenu(); e.Handled = true; };
        _edgeStrip.MouseRightButtonUp += (_, e) => { ShowDockMenu(); e.Handled = true; };
        _poll.Tick += (_, _) => { Refresh(); Evaluate(); DesktopIcons.Enforce(); };
        _hideDelay.Tick += (_, _) => { _hideDelay.Stop(); Evaluate(); };

        AllowDrop = true;
        Root.AllowDrop = true;
        Root.DragEnter += OnDragOver;
        Root.DragOver += OnDragOver;
        Root.DragLeave += (_, _) => { _dragLeave.Stop(); _dragLeave.Start(); };
        Root.Drop += OnDrop;
        _dragLeave.Tick += (_, _) => EndDrag();
        Theme.Changed += ApplyTheme;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd, noActivate: true);
        // O dock não reserva mais espaço: janelas maximizadas ocupam a tela toda e o dock se esconde sobre elas.

        ApplyTheme();
        Refresh();
        _poll.Start();
        _genie.Prepare();
        try { _gpu = new GpuGenie(Dispatcher); } catch { _gpu = null; } // sem GPU compatível: usa o plano B
        CreateEdgeSensor();

        _hookProc = (_, _, h, _, _, _, _) =>
        {
            if (_attention.Remove(h)) Dispatcher.BeginInvoke(UpdateAttention); // atendeu o pedido
            TaskbarController.OnForeground(h);
            WatchLocationOf(h);
            QueueEvaluate();
            Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        };
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        _minHookProc = (_, _, h, idObject, _, _, _) => { if (idObject == 0) OnMinimizeStart(h); };
        _minHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZESTART,
            IntPtr.Zero, _minHookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        TaskbarController.Enable();
        DesktopIcons.Hide();

        // Mesmos avisos que a barra de tarefas recebe (ex.: programa piscando pedindo atenção).
        _shellWindow = new ShellHookWindow(OnShellEvent);
        WatchLocationOf(Native.GetForegroundWindow());
        Evaluate();
    }

    /// <summary>
    /// Escuta mover/redimensionar/maximizar só do processo em foco (filtro feito pelo próprio Windows:
    /// nenhum custo com os demais programas).
    /// </summary>
    void WatchLocationOf(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _locPid || pid == Environment.ProcessId) return;
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _locPid = pid;
        _locHookProc ??= (_, _, h, idObject, _, _, _) =>
        {
            if (idObject == 0 && h == Native.GetForegroundWindow()) QueueEvaluate();
        };
        _locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locHookProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    void QueueEvaluate()
    {
        if (_evalQueued) return;
        _evalQueued = true;
        Dispatcher.BeginInvoke(() => { _evalQueued = false; Evaluate(); }, DispatcherPriority.Input);
    }

    public void Cleanup()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        if (_minHook != IntPtr.Zero) Native.UnhookWinEvent(_minHook);
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _shellWindow?.Dispose();
        DesktopIcons.Show();
        foreach (var h in _tuned) if (Native.IsWindow(h)) Native.RestoreNativeTransitions(h);
        TaskbarController.Disable();
    }

    // ------------------------------------------------------------ esconder automático (estilo macOS)
    /// <summary>Decide na hora se o dock deve aparecer. Chamado por eventos, não por timer.</summary>
    void Evaluate()
    {
        if (++_evalCount % 10 == 0) TaskbarController.Enforce();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);

        IntPtr fg = Native.GetForegroundWindow();
        bool covered = false, fullscreen = false;
        if (fg != IntPtr.Zero && fg != _hwnd && !Native.IsShellWindow(fg) && !Native.IsIconic(fg))
        {
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != Environment.ProcessId && Native.IsWindowVisible(fg) && Native.GetWindowRect(fg, out var wr))
            {
                // Tela cheia de verdade: cobre o monitor inteiro e não é uma janela maximizada com barra de título.
                long style = Native.GetWindowLongPtr(fg, -16).ToInt64();
                bool captionMax = Native.IsZoomed(fg) && (style & Native.WS_CAPTION) == Native.WS_CAPTION;
                fullscreen = !captionMax && wr.Left <= 0 && wr.Top <= 0 && wr.Right >= sw && wr.Bottom >= sh;

                if (Native.IsAppWindow(fg))
                {
                    _lastRect[fg] = wr;
                    var b = Native.VisibleBounds(fg);
                    double pillW = (BaseWidths().Sum() + 2 * PadX) * dpi;
                    int dockLeft = (int)((sw - pillW) / 2), dockRight = (int)((sw + pillW) / 2);
                    int dockTop = (int)(sh - (PillBottom + PillHeight) * dpi);
                    covered = Native.IsZoomed(fg) || (b.Bottom > dockTop && b.Right > dockLeft && b.Left < dockRight);
                }
            }
        }

        if (fullscreen != _fullscreen)
        {
            _fullscreen = fullscreen;
            Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
            FullscreenChanged?.Invoke(fullscreen);
        }
        if (fullscreen) { _edge?.Hide(); return; }

        Native.GetCursorPos(out var pt);
        bool atEdge = pt.Y >= sh - 2;
        bool attention = _attention.Count > 0 || _uacPending;
        if (!covered || atEdge || attention || _hovering || _dragging || _reordering || _menuOpen || _hideDelay.IsEnabled) SlideDock(false);
        else SlideDock(true);
    }

    void CreateEdgeSensor()
    {
        _edge = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            Background = Theme.Brush(0x01000000), // quase transparente, mas "pega" o mouse
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Title = "MacShell Edge",
        };
        _edge.SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(_edge).Handle, noActivate: true);
        _edge.MouseEnter += (_, _) => { _hideDelay.Stop(); _hideDelay.Start(); SlideDock(false); };
        _edge.AllowDrop = true;
        _edge.DragEnter += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; _hideDelay.Stop(); _hideDelay.Start(); SlideDock(false); };
        PlaceEdgeSensor();
    }

    void PlaceEdgeSensor()
    {
        if (_edge == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double sw = Native.GetSystemMetrics(0) / dpi, sh = Native.GetSystemMetrics(1) / dpi;
        double w = BaseWidths().Sum() + 2 * PadX + 80;
        _edge.Width = w;
        _edge.Height = 2 / dpi;
        _edge.Left = (sw - w) / 2;
        _edge.Top = sh - 2 / dpi;
    }

    double _lift; // quanto o dock sobe para não ficar atrás da barra de tarefas (quando ela aparece por um aviso)

    void SlideDock(bool hide, bool force = false)
    {
        if (hide == _dockHidden && !force) return;
        _dockHidden = hide;
        if (hide)
        {
            PlaceEdgeSensor(); _edge?.Show();
            // o dock desceu por baixo do mouse parado: o WPF não avisa que o mouse "saiu"
            _hovering = false; _hoverItem = null; StartAnimation();
        }
        else _edge?.Hide();

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int x = (int)Math.Round(Left * dpi);
        double from = double.IsNaN(_curYpx) ? Top * dpi : _curYpx;
        double to = (hide ? _hiddenTop : _shownTop - _lift) * dpi;
        IntPtr hwnd = _hwnd;
        _slide.Run(hide ? 0.20 : 0.16, t =>
        {
            double e = hide ? t * t * t : 1 - Math.Pow(1 - t, 3); // aparece desacelerando, some acelerando
            _curYpx = from + (to - from) * e;
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, (int)Math.Round(_curYpx), 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
        }, Dispatcher);
    }

    // ------------------------------------------------------------ efeito gênio
    void OnMinimizeStart(IntPtr h)
    {
        TaskbarController.EnforceSoon();
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == Environment.ProcessId) return;
        if (!_lastRect.TryGetValue(h, out var from) && !TryNormalRect(h, out from)) return;
        var icon = IconRectFor(h);

        if (_gpu?.Ready == true)
        {
            from = ClampToScreen(from);
            var snap = _genie.CaptureStill(h, from, CornerRadiusFor(from));
            if (snap != null)
            {
                StoreSnapshot(h, snap);
                _gpu.Play(snap, from, icon, restore: false, firstFrame: _genie.HideStill);
                return;
            }
            _genie.HideStill();
        }
        _genie.Play(h, from, icon, restore: false); // plano B: faixas do DWM
    }

    /// <summary>Restaura uma janela minimizada saindo de dentro do ícone.</summary>
    void RestoreAnimated(IntPtr h)
    {
        if (!Native.IsIconic(h)) { Native.ForceForeground(h); return; }
        if (!_lastRect.TryGetValue(h, out var to) && !TryNormalRect(h, out to)) { Native.ForceForeground(h); return; }
        Native.DisableNativeTransitions(h);

        if (_gpu?.Ready == true && _snapshots.TryGetValue(h, out var snap))
        {
            DropSnapshot(h);
            _gpu.Play(snap, ClampToScreen(to), IconRectFor(h), restore: true, done: () => Native.ForceForeground(h));
            return;
        }
        _genie.Play(h, to, IconRectFor(h), restore: true, done: () => Native.ForceForeground(h));
    }

    // ---- fotos das janelas minimizadas (para a volta), com limite de memória
    readonly Dictionary<IntPtr, WindowSnapshot> _snapshots = new();
    readonly LinkedList<IntPtr> _snapOrder = new();
    const long SnapshotBudget = 40L * 1024 * 1024;

    void StoreSnapshot(IntPtr h, WindowSnapshot s)
    {
        DropSnapshot(h);
        _snapshots[h] = s;
        _snapOrder.AddLast(h);
        long total = _snapshots.Values.Sum(x => (long)x.Pixels.Length);
        while (total > SnapshotBudget && _snapOrder.First != null && _snapOrder.First.Value != h)
        {
            var old = _snapOrder.First.Value;
            total -= _snapshots[old].Pixels.Length;
            DropSnapshot(old);
        }
    }

    void DropSnapshot(IntPtr h)
    {
        if (_snapshots.Remove(h)) _snapOrder.Remove(h);
    }

    static Native.RECT ClampToScreen(Native.RECT r)
    {
        int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
        return new Native.RECT
        {
            Left = Math.Max(0, r.Left), Top = Math.Max(0, r.Top),
            Right = Math.Min(sw, r.Right), Bottom = Math.Min(sh, r.Bottom),
        };
    }

    /// <summary>Janelas normais do Windows 11 têm cantos de 8 px; maximizadas, cantos retos.</summary>
    float CornerRadiusFor(Native.RECT r)
    {
        var work = Native.WorkArea();
        bool maximized = r.Left <= work.Left && r.Top <= work.Top && r.Right >= work.Right && r.Bottom >= work.Bottom - 2;
        return maximized ? 0 : (float)(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>Posição "aberta" de uma janela minimizada, pela posição salva pelo Windows.</summary>
    static bool TryNormalRect(IntPtr h, out Native.RECT r)
    {
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        r = default;
        if (!Native.GetWindowPlacement(h, ref wp)) return false;
        var work = Native.WorkArea();
        if ((wp.flags & 2) != 0) { r = work; return true; } // WPF_RESTORETOMAXIMIZED
        r = wp.rcNormalPosition;
        // rcNormalPosition é relativo à área útil
        r.Left += work.Left; r.Right += work.Left; r.Top += work.Top; r.Bottom += work.Top;
        return r.Right > r.Left && r.Bottom > r.Top;
    }

    /// <summary>Retângulo (px de tela) do ícone do app dessa janela — como se o dock estivesse visível.</summary>
    Native.RECT IconRectFor(IntPtr h)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        DockItem? item = _views.Values.FirstOrDefault(v => v.Windows.Contains(h));
        if (item == null)
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            string? path = PathOf(Native.ResolvePid(h, pid, PathOf(pid)));
            if (path != null) _views.TryGetValue(path, out item);
        }

        double cx, top, size = IconSize;
        if (item != null && item.IsLoaded)
        {
            var p = item.TranslatePoint(new Point(item.ActualWidth / 2, 0), Root);
            size = IconSize * item.Scale;
            cx = Left + p.X;
            top = _shownTop + p.Y;
        }
        else
        {
            cx = Left + Width / 2;
            top = _shownTop + WinHeight - PillBottom - PillHeight + PadTop;
        }
        return new Native.RECT
        {
            Left = (int)((cx - size / 2) * dpi), Right = (int)((cx + size / 2) * dpi),
            Top = (int)(top * dpi), Bottom = (int)((top + size) * dpi),
        };
    }

    // ------------------------------------------------------------ tema
    bool _light;
    void ApplyTheme()
    {
        _light = Theme.IsLight;
        Pill.Background = Theme.Brush(_light ? 0xC4F4F4F6U : 0xBF262628U);
        Pill.BorderBrush = Theme.Brush(_light ? 0x26000000U : 0x33FFFFFFU);
        PillHighlight.BorderBrush = Theme.Brush(_light ? 0x80FFFFFFU : 0x14FFFFFFU);
        Label.Background = Theme.Brush(_light ? 0xF2F6F6F6U : 0xE6303032U);
        Label.BorderBrush = Theme.Brush(_light ? 0x26000000U : 0x33FFFFFFU);
        LabelText.Foreground = Theme.Brush(_light ? 0xFF1D1D1FU : 0xFFF5F5F7U);
        foreach (var v in _views.Values) v.ApplyTheme(_light);
    }

    // ------------------------------------------------------------ apps abertos
    void Refresh()
    {
        if (++_refreshCount % 25 == 0)
        {
            _pathCache.Clear();
            _tuned.RemoveWhere(h => !Native.IsWindow(h));
            foreach (var dead in _lastRect.Keys.Where(h => !Native.IsWindow(h)).ToList()) _lastRect.Remove(dead);
        }
        uint self = (uint)Environment.ProcessId;
        var groups = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsAppWindow(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            string? path = PathOf(pid);
            uint real = Native.ResolvePid(h, pid, path);
            if (real != pid) path = PathOf(real) ?? path;
            if (path == null) return true;
            if (!groups.TryGetValue(path, out var list)) { groups[path] = list = new(); order.Add(path); }
            list.Add(h);
            if (_tuned.Add(h)) Native.DisableNativeTransitions(h); // a animação gênio substitui a nativa
            if (!Native.IsIconic(h) && Native.GetWindowRect(h, out var wr)) _lastRect[h] = wr;
            return true;
        }, IntPtr.Zero);

        var pinnedKeys = new HashSet<string>(_pinned.Select(KeyOf), StringComparer.OrdinalIgnoreCase);
        _runningOrder.RemoveAll(k => !groups.ContainsKey(k) || pinnedKeys.Contains(k));
        foreach (var k in order) if (!pinnedKeys.Contains(k) && !_runningOrder.Contains(k, StringComparer.OrdinalIgnoreCase)) _runningOrder.Add(k);

        var keys = new List<string>();
        keys.AddRange(_pinned.Select(KeyOf));
        if (_runningOrder.Count > 0) { keys.Add(SepKey + "1"); keys.AddRange(_runningOrder); }
        keys.Add(SepKey + "2");
        keys.Add(TrashKey);

        if (!_reordering && !_dragging && !keys.SequenceEqual(_currentKeys, StringComparer.OrdinalIgnoreCase))
        {
            Rebuild(keys);
            _currentKeys = keys;
        }

        foreach (var k in keys)
        {
            if (!_views.TryGetValue(k, out var v) || v.Kind != ItemKind.App) continue;
            v.Windows = groups.TryGetValue(k, out var w) ? w : new List<IntPtr>();
            v.SetRunning(v.Windows.Count > 0);
            if (v.Windows.Count > 0 && !v.Attention) v.StopBounce();
        }

        // Pedido de administrador esperando resposta (pode estar escondido atrás das janelas).
        // (garantia extra: o aviso de "piscar" já chega na hora; aqui confere a cada ~6 s, custo ~3 ms)
        if (_refreshCount % 5 == 1 || _uacPending)
        {
            var consent = System.Diagnostics.Process.GetProcessesByName("consent");
            _uacPending = consent.Length > 0;
            foreach (var c in consent) c.Dispose();
        }
        UpdateAttention();
    }

    void OnShellEvent(int code, IntPtr h)
    {
        if (code != Native.HSHELL_FLASH) return;
        if (h != IntPtr.Zero && h != Native.GetForegroundWindow())
        {
            _attention.Add(h);
            Dispatcher.BeginInvoke(UpdateAttention);
        }
    }

    /// <summary>
    /// Como no Mac: o ícone do app que pediu atenção fica pulando no dock, o dock aparece e a barra de
    /// tarefas também (lá o botão piscando é clicável). Tudo volta ao normal quando o pedido é atendido.
    /// </summary>
    void UpdateAttention()
    {
        IntPtr fg = Native.GetForegroundWindow();
        _attention.RemoveWhere(h => h == fg || !Native.IsWindow(h));

        foreach (var v in _views.Values)
        {
            bool wants = v.Kind == ItemKind.App && v.Windows.Any(_attention.Contains);
            if (wants && !v.Attention) { v.Attention = true; v.BounceForever(); }
            else if (!wants && v.Attention) { v.Attention = false; v.StopBounce(); }
        }

        bool any = _attention.Count > 0 || _uacPending;
        TaskbarController.SetAttention(any);

        // Com a barra de tarefas visível, o dock sobe para ficar acima dela.
        double lift = 0;
        if (any)
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr))
                lift = Math.Max(0, Native.GetSystemMetrics(1) - tr.Top) / VisualTreeHelper.GetDpi(this).DpiScaleY;
        }
        bool liftChanged = Math.Abs(lift - _lift) > 0.5;
        _lift = lift;
        if (any || liftChanged) SlideDock(false, force: liftChanged);
    }

    string? PathOf(uint pid)
    {
        if (!_pathCache.TryGetValue(pid, out var p)) _pathCache[pid] = p = Native.GetProcessPath(pid);
        return p;
    }

    static string KeyOf(PinnedApp p) => p.Target ?? p.Launch;

    void Rebuild(List<string> keys)
    {
        Items.Children.Clear();
        var live = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        foreach (var dead in _views.Keys.Where(k => !live.Contains(k)).ToList()) _views.Remove(dead);

        foreach (var k in keys)
        {
            if (!_views.TryGetValue(k, out var v))
            {
                if (k.StartsWith(SepKey)) v = new DockItem(ItemKind.Separator, k, "", null);
                else if (k == TrashKey) v = new DockItem(ItemKind.Trash, k, "Lixeira", IconLoader.Get(RecycleBin));
                else
                {
                    var pin = _pinned.FirstOrDefault(p => string.Equals(KeyOf(p), k, StringComparison.OrdinalIgnoreCase));
                    string name = pin?.Name ?? AppInfo.FriendlyName(k);
                    v = new DockItem(ItemKind.App, k, name, IconLoader.Get(pin?.Launch ?? k)) { Pin = pin };
                    if (v.Icon == null && pin?.Target != null) v.SetIcon(IconLoader.Get(pin.Target));
                }
                v.ApplyTheme(_light);
                v.MouseEnter += (_, _) => { _hoverItem = v; StartAnimation(); };
                v.MouseLeave += (_, _) => { if (_hoverItem == v) _hoverItem = null; };
                v.PreviewMouseLeftButtonDown += (_, e) => { if (v.Kind == ItemKind.App) { _pressItem = v; _pressPos = e.GetPosition(Root); } };
                v.MouseLeftButtonUp += (_, e) => { OnClick(v); e.Handled = true; };
                v.MouseRightButtonUp += (_, e) => { ShowItemMenu(v); e.Handled = true; };
                _views[k] = v;
            }
            else
            {
                v.Pin = _pinned.FirstOrDefault(p => string.Equals(KeyOf(p), k, StringComparison.OrdinalIgnoreCase));
            }
            Items.Children.Add(v);
        }
        Layout(true);
        PlaceWindow();
    }

    // ------------------------------------------------------------ posição da janela
    void PlaceWindow()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double sw = Native.GetSystemMetrics(0) / dpi, sh = Native.GetSystemMetrics(1) / dpi;
        double baseWidth = BaseWidths().Sum() + 2 * PadX;
        Width = Math.Min(sw, baseWidth + 420);
        Left = (sw - Width) / 2;
        _shownTop = sh - WinHeight;
        _hiddenTop = _shownTop + Reserve + 8;
        _slide.Cancel();
        Top = _dockHidden ? _hiddenTop : _shownTop;
        _curYpx = Top * VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    IEnumerable<double> BaseWidths()
    {
        foreach (DockItem v in Items.Children)
            yield return v.Kind == ItemKind.Separator ? SepWidth
                       : v.Kind == ItemKind.Placeholder ? (IconSize + Gap) * v.Grow
                       : IconSize + Gap;
    }

    // ------------------------------------------------------------ efeito lupa (só roda enquanto o mouse mexe)
    void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Confere pela posição real do cursor (não pelo estado do WPF, que pode ficar velho).</summary>
    bool CursorOverDock()
    {
        if (!Native.GetCursorPos(out var pt)) return false;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double top = double.IsNaN(_curYpx) ? Top : _curYpx / dpi;
        double x = pt.X / dpi - Left, y = pt.Y / dpi - top;
        double pillLeft = (ActualWidth - Pill.ActualWidth) / 2;
        double tallest = Items.Children.OfType<DockItem>().Select(i => i.ActualHeight).DefaultIfEmpty(0).Max();
        double areaTop = ActualHeight - PillBottom - PadBottom - tallest - 4;
        return x >= pillLeft - 2 && x <= pillLeft + Pill.ActualWidth + 2 && y >= areaTop && y <= ActualHeight;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.05);
        _lastFrame = now;
        if (_hovering && !_dragging && !_reordering && !CursorOverDock()) { _hovering = false; _hoverItem = null; }
        bool settled = Layout(false, dt);
        UpdateLabel();
        if (settled && !_hovering && !_dragging)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    /// <returns>true quando todos os ícones chegaram ao tamanho final.</returns>
    bool Layout(bool snap, double dt = 0)
    {
        int n = Items.Children.Count;
        if (n == 0) return true;
        var widths = BaseWidths().ToArray();
        double total = widths.Sum();
        bool magnify = _hovering || _dragging;
        double mouseX = _dragging ? _dragX : _hovering ? Mouse.GetPosition(Root).X - Root.ActualWidth / 2 : double.NaN;
        double k = snap ? 1 : 1 - Math.Exp(-dt * 20);
        bool settled = true;
        List<DockItem>? closed = null;

        double x = -total / 2;
        double pillWidth = 2 * PadX;
        for (int i = 0; i < n; i++)
        {
            var v = (DockItem)Items.Children[i];
            double center = x + widths[i] / 2;
            x += widths[i];

            double target = 1;
            if (v.Kind == ItemKind.Placeholder)
            {
                double goal = v.Closing ? 0 : 1;           // o espaço abre/fecha suave
                double g = v.Grow + (goal - v.Grow) * k;
                if (Math.Abs(goal - g) < 0.003) g = goal; else settled = false;
                v.Grow = g;
                if (v.Closing && g <= 0.003) closed ??= new List<DockItem>();
                if (v.Closing && g <= 0.003) closed!.Add(v);
            }
            if (magnify && v.Kind != ItemKind.Separator)
            {
                double d = (mouseX - center) / Sigma;
                if (Math.Abs(d) < 3.2) target = 1 + (MaxScale - 1) * Math.Exp(-d * d / 2);
            }
            double s = v.Scale + (target - v.Scale) * k;
            if (Math.Abs(target - s) < 0.002) s = target; else settled = false;
            v.SetScale(s);
            pillWidth += v.Kind == ItemKind.Separator ? SepWidth
                       : v.Kind == ItemKind.Placeholder ? (IconSize * s + Gap) * v.Grow
                       : IconSize * s + Gap;
        }
        if (closed != null) foreach (var c in closed) Items.Children.Remove(c);
        Pill.Width = pillWidth;
        PillHighlight.Width = pillWidth - 2;
        _edgeStrip.Width = pillWidth;
        return settled;
    }

    void UpdateLabel()
    {
        var v = _hoverItem;
        if (v == null || !_hovering || v.Kind == ItemKind.Separator || ContextOpen)
        {
            Label.Visibility = Visibility.Collapsed;
            return;
        }
        LabelText.Text = v.AppName;
        Label.Visibility = Visibility.Visible;
        Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var p = v.TranslatePoint(new Point(v.ActualWidth / 2, 0), Root);
        Canvas.SetLeft(Label, Math.Round(p.X - Label.DesiredSize.Width / 2));
        Canvas.SetTop(Label, Math.Round(p.Y - Label.DesiredSize.Height - 8));
    }

    bool ContextOpen => ContextMenuService.GetContextMenu(this) is { IsOpen: true } || _menuOpen;
    bool _menuOpen;

    // ------------------------------------------------------------ arrastar atalhos para fixar
    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Link;
        _dragLeave.Stop();
        _dragging = true;
        var pos = e.GetPosition(Root);
        _dragX = pos.X - Root.ActualWidth / 2;
        UpdatePlaceholder(pos.X);
        SlideDock(false);
        StartAnimation();
    }

    /// <summary>Abre um espaço entre os apps fixados, onde o atalho vai entrar.</summary>
    void UpdatePlaceholder(double xRoot)
    {
        int slot = 0;
        foreach (DockItem v in Items.Children)
        {
            if (v.Kind != ItemKind.App || v.Pin == null) continue;
            double c = v.TranslatePoint(new Point(v.ActualWidth / 2, 0), Root).X;
            if (c < xRoot) slot++;
        }
        MovePlaceholderTo(slot);
    }

    /// <summary>O espaço velho fecha devagar enquanto o novo abre — os ícones deslizam, sem pulos.</summary>
    void MovePlaceholderTo(int slot)
    {
        _dropIndex = slot;
        if (_placeholder != null && SlotOf(_placeholder) == slot) return;
        ClosePlaceholder();
        _placeholder = new DockItem(ItemKind.Placeholder, "|placeholder", "", null);
        Items.Children.Insert(ChildIndexForSlot(slot), _placeholder);
        StartAnimation();
    }

    void ClosePlaceholder()
    {
        if (_placeholder != null) { _placeholder.Closing = true; _placeholder = null; StartAnimation(); }
    }

    void ClearPlaceholders()
    {
        foreach (var ph in Items.Children.OfType<DockItem>().Where(v => v.Kind == ItemKind.Placeholder).ToList())
            Items.Children.Remove(ph);
        _placeholder = null;
    }

    int SlotOf(DockItem ph)
    {
        int count = 0;
        foreach (DockItem v in Items.Children)
        {
            if (v == ph) return count;
            if (v.Kind == ItemKind.App && v.Pin != null) count++;
        }
        return -1;
    }

    int ChildIndexForSlot(int slot)
    {
        int count = 0;
        for (int i = 0; i < Items.Children.Count; i++)
        {
            var v = (DockItem)Items.Children[i];
            if (v.Kind == ItemKind.Placeholder) continue;
            if (count == slot) return i;
            if (v.Kind == ItemKind.App && v.Pin != null) count++;
            else return i; // acabaram os fixados
        }
        return Items.Children.Count;
    }

    double PinnedZoneRight()
    {
        double right = 0;
        foreach (DockItem v in Items.Children)
            if ((v.Kind == ItemKind.App && v.Pin != null) || (v.Kind == ItemKind.Placeholder && !v.Closing))
                right = Math.Max(right, v.TranslatePoint(new Point(v.ActualWidth, 0), Root).X);
        return right;
    }

    // ------------------------------------------------------------ reordenar arrastando dentro do dock
    void OnRootMouseMove(MouseEventArgs e)
    {
        _hovering = true;
        StartAnimation();
        var p = e.GetPosition(Root);
        if (_reordering) { MoveReorder(p); return; }
        if (_pressItem != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (Math.Abs(p.X - _pressPos.X) > 6 || Math.Abs(p.Y - _pressPos.Y) > 6) BeginReorder(_pressItem, p);
        }
        else if (e.LeftButton != MouseButtonState.Pressed) _pressItem = null;
    }

    void BeginReorder(DockItem v, Point p)
    {
        _pressItem = null;
        _reordering = true;
        _reorderItem = v;

        // o ícone "descola" do dock e segue o mouse
        double size = IconSize * v.Scale;
        var topLeft = v.TranslatePoint(new Point((v.ActualWidth - size) / 2, 0), Root);
        _ghostOffset = new Point(p.X - topLeft.X, p.Y - topLeft.Y);
        _ghost = new Image { Source = v.Icon, Width = size, Height = size, Opacity = 0.9, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_ghost, BitmapScalingMode.HighQuality);
        Overlay.Children.Add(_ghost);

        // no lugar dele fica um espaço do mesmo tamanho (nada pula)
        int idx = Items.Children.IndexOf(v);
        Items.Children.RemoveAt(idx);
        var home = new DockItem(ItemKind.Placeholder, "|placeholder", "", null) { Grow = 1 };
        Items.Children.Insert(idx, home);
        if (v.Pin != null) { _placeholder = home; _dropIndex = _pinned.IndexOf(v.Pin); }
        else { home.Closing = true; _placeholder = null; _dropIndex = -1; } // app aberto não fixado: pode virar fixo

        _hoverItem = null;
        Label.Visibility = Visibility.Collapsed;
        Root.CaptureMouse();
        MoveReorder(p);
    }

    void MoveReorder(Point p)
    {
        if (_ghost != null)
        {
            Canvas.SetLeft(_ghost, p.X - _ghostOffset.X);
            Canvas.SetTop(_ghost, p.Y - _ghostOffset.Y);
        }
        // app aberto (não fixado) só entra se for levado até a área dos fixados
        if (_reorderItem?.Pin == null && p.X > PinnedZoneRight() + IconSize)
        {
            ClosePlaceholder();
            _dropIndex = -1;
            return;
        }
        UpdatePlaceholder(p.X);
    }

    void EndReorder(bool commit)
    {
        if (!_reordering) return;
        _reordering = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        if (_ghost != null) { Overlay.Children.Remove(_ghost); _ghost = null; }
        var v = _reorderItem;
        _reorderItem = null;
        int slot = _dropIndex;
        ClearPlaceholders();

        if (commit && v != null && slot >= 0)
        {
            if (v.Pin != null)
            {
                _pinned.Remove(v.Pin);
                _pinned.Insert(Math.Clamp(slot, 0, _pinned.Count), v.Pin);
            }
            else
            {
                _pinned.Insert(Math.Clamp(slot, 0, _pinned.Count), new PinnedApp { Name = v.AppName, Launch = v.Key, Target = v.Key });
            }
            DockConfig.Save(_pinned);
        }
        _currentKeys.Clear();
        Refresh();
        StartAnimation();
    }

    void EndDrag()
    {
        _dragLeave.Stop();
        _dragging = false;
        ClearPlaceholders();
        _hovering = Root.IsMouseOver;
        StartAnimation();
        _hideDelay.Stop(); _hideDelay.Start();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int idx = _placeholder != null ? _dropIndex : _pinned.Count;
        EndDrag();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (var file in files)
        {
            var pin = PinnedApp.FromPath(file);
            if (pin == null) continue;
            int existing = _pinned.FindIndex(p => string.Equals(KeyOf(p), KeyOf(pin), StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _pinned.RemoveAt(existing);          // já estava fixado: só muda de lugar
                if (existing < idx) idx--;
            }
            idx = Math.Clamp(idx, 0, _pinned.Count);
            _pinned.Insert(idx, pin);
            idx++;
        }
        DockConfig.Save(_pinned);
        _currentKeys.Clear();
        Refresh();
    }

    // ------------------------------------------------------------ cliques
    void OnClick(DockItem v)
    {
        switch (v.Kind)
        {
            case ItemKind.Trash:
                Launch("explorer.exe", "shell:RecycleBinFolder");
                break;
            case ItemKind.App when v.Windows.Count > 0:
                IntPtr fg = Native.GetForegroundWindow();
                if (v.Windows.Contains(fg))
                {
                    if (v.Windows.Count == 1) Native.ShowWindow(fg, Native.SW_MINIMIZE);
                    else Native.ForceForeground(v.Windows[^1]); // alterna entre as janelas do app
                }
                else RestoreAnimated(v.Windows.FirstOrDefault(w => !Native.IsIconic(w)) is { } open && open != IntPtr.Zero ? open : v.Windows[0]);
                break;
            case ItemKind.App:
                v.Bounce();
                Launch(v.Pin?.Launch ?? v.Key);
                break;
        }
    }

    static void Launch(string file, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não consegui abrir:\n" + file + "\n\n" + ex.Message, "MacShell");
        }
    }

    // ------------------------------------------------------------ menus
    void ShowItemMenu(DockItem v)
    {
        if (v.Kind == ItemKind.Separator) { ShowDockMenu(); return; }
        var m = NewMenu(v);

        if (v.Kind == ItemKind.Trash)
        {
            m.Items.Add(Item("Abrir", () => OnClick(v)));
            m.Items.Add(Item("Esvaziar Lixeira…", () => SHEmptyRecycleBin(_hwnd, null, 0)));
            m.IsOpen = true;
            return;
        }

        foreach (var h in v.Windows.Take(10))
        {
            string t = Native.GetTitle(h);
            if (t.Length > 48) t = t[..47] + "…";
            m.Items.Add(Item((Native.IsIconic(h) ? "◦ " : "• ") + t, () => RestoreAnimated(h)));
        }
        if (v.Windows.Count > 0) m.Items.Add(new Separator());

        m.Items.Add(Item(v.Windows.Count > 0 ? "Nova janela" : "Abrir", () => { v.Bounce(); Launch(v.Pin?.Launch ?? v.Key); }));
        if (v.Pin != null) m.Items.Add(Item("Remover do Dock", () => Unpin(v)));
        else m.Items.Add(Item("Manter no Dock", () => PinApp(v)));

        string? file = v.Pin?.Target ?? (v.Pin == null ? v.Key : null);
        if (file != null && File.Exists(file))
            m.Items.Add(Item("Mostrar no Explorador", () => Launch("explorer.exe", $"/select,\"{file}\"")));

        if (v.Windows.Count > 0)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Item("Encerrar", () =>
            {
                foreach (var h in v.Windows) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }));
        }
        m.IsOpen = true;
    }

    void ShowDockMenu()
    {
        var m = NewMenu(Pill);
        m.Items.Add(Item("Editar apps fixados (dock.json)", () => Launch("notepad.exe", System.IO.Path.Combine(DockConfig.Dir, "dock.json"))));
        m.Items.Add(Item("Recarregar dock", () => { _pinned = DockConfig.Load(); _currentKeys.Clear(); Refresh(); }));
        m.Items.Add(Item(TaskbarController.Enabled ? "Mostrar barra de tarefas do Windows" : "Esconder barra de tarefas do Windows",
            () => { if (TaskbarController.Enabled) TaskbarController.Disable(); else TaskbarController.Enable(); }));
        m.Items.Add(Item(DesktopIcons.Hidden ? "Mostrar ícones da área de trabalho" : "Esconder ícones da área de trabalho",
            () => { if (DesktopIcons.Hidden) DesktopIcons.Show(); else DesktopIcons.Hide(); }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Sair do MacShell", App.Quit));
        m.IsOpen = true;
    }

    ContextMenu NewMenu(UIElement target)
    {
        var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, VerticalOffset = -6 };
        _menuOpen = true;
        Label.Visibility = Visibility.Collapsed;
        m.Closed += (_, _) => _menuOpen = false;
        return m;
    }

    static MenuItem Item(string text, Action click)
    {
        var mi = new MenuItem { Header = text };
        mi.Click += (_, _) => click();
        return mi;
    }

    void PinApp(DockItem v)
    {
        _pinned.Add(new PinnedApp { Name = v.AppName, Launch = v.Key, Target = v.Key });
        DockConfig.Save(_pinned);
        Refresh();
    }

    void Unpin(DockItem v)
    {
        _pinned.Remove(v.Pin!);
        DockConfig.Save(_pinned);
        _views.Remove(v.Key);
        Refresh();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
}

// ================================================================ um ícone do dock
internal enum ItemKind { App, Separator, Trash, Placeholder }

internal sealed class DockItem : Grid
{
    const double IconSize = 50, Gap = 7, DotArea = 8;

    public ItemKind Kind { get; }
    public string Key { get; }
    public string AppName { get; }
    public PinnedApp? Pin { get; set; }
    public List<IntPtr> Windows { get; set; } = new();
    public double Scale { get; private set; } = 1;
    /// <summary>Só para o espaço vazio de arrastar: 0 = fechado, 1 = aberto.</summary>
    public double Grow { get; set; }
    /// <summary>Espaço que está fechando (some quando chega a 0).</summary>
    public bool Closing { get; set; }
    public ImageSource? Icon => _image?.Source;

    readonly Image? _image;
    readonly Ellipse? _dot;
    readonly Rectangle? _line;
    readonly TranslateTransform _bounce = new();

    public DockItem(ItemKind kind, string key, string name, ImageSource? icon)
    {
        Kind = kind; Key = key; AppName = name;
        Background = Brushes.Transparent; // área clicável inteira
        VerticalAlignment = VerticalAlignment.Bottom;

        if (kind == ItemKind.Placeholder)
        {
            Background = null;
            IsHitTestVisible = false;
            Width = 0;
            Height = IconSize + DotArea;
            return;
        }

        if (kind == ItemKind.Separator)
        {
            Width = 15;
            Height = IconSize + DotArea;
            _line = new Rectangle { Width = 1, Height = IconSize * 0.78, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, IconSize * 0.11, 0, 0) };
            Children.Add(_line);
            Background = null;
            return;
        }

        _image = new Image
        {
            Source = icon,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = _bounce,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _dot = new Ellipse { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1), Visibility = Visibility.Hidden };
        Children.Add(_image);
        Children.Add(_dot);
        SetScale(1);
    }

    public void SetIcon(ImageSource? src) { if (_image != null) _image.Source = src; }

    public void ApplyTheme(bool light)
    {
        if (_line != null) _line.Fill = Theme.Brush(light ? 0x40000000U : 0x4DFFFFFFU);
        if (_dot != null) _dot.Fill = Theme.Brush(light ? 0xCC000000U : 0xE6FFFFFFU);
    }

    public void SetScale(double s)
    {
        Scale = s;
        if (Kind == ItemKind.Placeholder) { Width = (IconSize * s + Gap) * Grow; return; }
        if (Kind == ItemKind.Separator || _image == null) return;
        double size = IconSize * s;
        Width = size + Gap;
        Height = size + DotArea;
        _image.Width = size;
        _image.Height = size;
    }

    public void SetRunning(bool running)
    {
        if (_dot != null) _dot.Visibility = running ? Visibility.Visible : Visibility.Hidden;
    }

    bool _bouncing;
    public void Bounce()
    {
        if (_bouncing) return;
        _bouncing = true;
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(3) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(460)), new BounceEase { Bounces = 1, Bounciness = 4, EasingMode = EasingMode.EaseOut }));
        a.Completed += (_, _) => _bouncing = false;
        _bounce.BeginAnimation(TranslateTransform.YProperty, a);
    }

    public void StopBounce()
    {
        if (!_bouncing) return;
        _bouncing = false;
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>App pedindo atenção: pula, pausa, pula... até ser aberto.</summary>
    public bool Attention { get; set; }

    public void BounceForever()
    {
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
        _bouncing = true;
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-26, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)), new BounceEase { Bounces = 1, Bounciness = 4, EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        _bounce.BeginAnimation(TranslateTransform.YProperty, a);
    }
}
