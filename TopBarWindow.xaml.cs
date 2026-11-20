using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacShell;

public partial class TopBarWindow : Window
{
    const double BarHeight = 28;
    static readonly CultureInfo PtBr = new("pt-BR");

    AppBar? _appBar;
    IntPtr _hwnd;
    IntPtr _hook;
    Native.WinEventDelegate? _hookProc; // mantém o delegate vivo
    IntPtr _activeWindow;
    uint _activePid;
    string? _activePath;
    readonly DispatcherTimer _clock = new();

    public void SetFullscreen(bool full) => Visibility = full ? Visibility.Hidden : Visibility.Visible;

    public TopBarWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        LogoBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN);
        SearchBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN, Native.VK_MENU, Native.VK_SPACE);
        ControlBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN, 0x41 /* A */);
        NetBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN, 0x41);
        SoundBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN, 0x41);
        BatteryBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN, 0x41);
        ClockBtn.Click += (_, _) => Native.SendKeys(Native.VK_LWIN, 0x4E /* N */);
        AppBtn.Click += (_, _) => ShowAppMenu();
        WindowBtn.Click += (_, _) => ShowWindowMenu();

        _clock.Tick += (_, _) => UpdateClock();
        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNetwork);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNetwork);
        Theme.Changed += ApplyTheme;
        // trocou o papel de parede: refaz o vidro
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop) Dispatcher.BeginInvoke(ApplyTheme);
        };
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd, noActivate: true);

        _appBar = new AppBar(this, AppBar.ABE_TOP, BarHeight);
        _appBar.Register();
        // O aviso de "tela cheia" do próprio Windows dispara errado (ao minimizar/maximizar ou com a área de trabalho em foco).
        // Quem decide agora é o dock, que confere se a janela realmente cobre o monitor inteiro.
        PlaceFromAppBar();

        ApplyTheme();
        UpdateClock();
        UpdateNetwork();
        UpdateBattery();

        _hookProc = OnForegroundChanged;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        UpdateActiveApp(Native.GetForegroundWindow());
        ContentRendered += (_, _) => Relayout();
    }

    void PlaceFromAppBar()
    {
        double s = VisualTreeHelperDpi;
        var r = _appBar!.Rect;
        Left = r.Left / s;
        Top = r.Top / s;
        Width = (r.Right - r.Left) / s;
        Height = BarHeight;
    }

    double VisualTreeHelperDpi => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>
    /// Janelas transparentes (layered) às vezes exibem o quadro anterior até o próximo redesenho.
    /// Força medir + redesenhar logo depois de trocar um texto.
    /// </summary>
    void Relayout() => Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
    {
        Root.InvalidateMeasure();
        UpdateLayout();
        Root.InvalidateVisual();
    });

    public void Cleanup()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _appBar?.Remove();
    }

    // ------------------------------------------------------------ visual
    void ApplyTheme()
    {
        bool light = Theme.IsLight;
        Foreground = Theme.Brush(light ? 0xFF1A1A1AU : 0xFFF5F5F7U);
        BottomLine.Background = Theme.Brush(light ? 0x1A000000U : 0x1AFFFFFFU);
        // Vidro fosco: a faixa do papel de parede atrás da barra, desfocada (como no macOS).
        // Sem efeitos do Windows por cima — eles escureciam a barra e travavam o redesenho.
        double width = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        Root.Background = WallpaperBackdrop.Build(width, BarHeight, light)
                          ?? Theme.Brush(light ? 0xE6F2F2F2U : 0xE61E1E1EU);
        Relayout();
    }

    // ------------------------------------------------------------ relógio (acorda 1x por minuto)
    void UpdateClock()
    {
        var now = DateTime.Now;
        string day = now.ToString("ddd", PtBr).TrimEnd('.');
        string mon = now.ToString("MMM", PtBr).TrimEnd('.');
        Clock.Text = $"{day} {now.Day} de {mon}  {now:HH:mm}";
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(30);
        _clock.Start();
        UpdateBattery();
        Relayout();
    }

    // ------------------------------------------------------------ rede (só por evento, sem polling)
    void UpdateNetwork()
    {
        string glyph = ""; // sem rede
        string tip = "Sem conexão";
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) { glyph = ""; tip = "Wi-Fi conectado"; break; }
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) { glyph = ""; tip = "Cabo de rede conectado"; }
            }
        }
        catch { }
        NetIcon.Text = glyph;
        NetBtn.ToolTip = tip;
        Relayout();
    }

    void UpdateBattery()
    {
        if (!Native.GetSystemPowerStatus(out var ps) || ps.BatteryFlag == 128 || ps.BatteryFlag == 255 || ps.BatteryLifePercent > 100)
        {
            BatteryBtn.Visibility = Visibility.Collapsed;
            return;
        }
        int pct = ps.BatteryLifePercent;
        BatteryBtn.Visibility = Visibility.Visible;
        BatteryText.Text = pct + "%";
        bool charging = ps.ACLineStatus == 1;
        int level = Math.Clamp(pct / 10, 0, 10);
        BatteryIcon.Text = ((char)((charging ? 0xEBAB : 0xEBA0) + level)).ToString();
    }

    // ------------------------------------------------------------ app em foco
    void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => UpdateActiveApp(hwnd);

    void UpdateActiveApp(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == Environment.ProcessId) return; // clique no próprio dock/barra

        string? path = Native.GetProcessPath(pid);
        uint realPid = Native.ResolvePid(h, pid, path);
        if (realPid != pid) { pid = realPid; path = Native.GetProcessPath(pid) ?? path; }

        string name;
        if (Native.IsShellWindow(h)) name = "Explorador de Arquivos";
        else if (path == null) name = Native.GetTitle(h);
        else if (path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) name = Native.GetTitle(h);
        else name = AppInfo.FriendlyName(path);

        if (string.IsNullOrWhiteSpace(name)) return;
        _activeWindow = Native.IsShellWindow(h) ? IntPtr.Zero : h;
        _activePid = pid;
        _activePath = path;
        AppName.Text = name;
        Relayout();
    }

    // ------------------------------------------------------------ menus
    void ShowAppMenu()
    {
        var m = new ContextMenu { PlacementTarget = AppBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool hasApp = _activeWindow != IntPtr.Zero && _activePath != null;

        m.Items.Add(Item("Ocultar " + AppName.Text, hasApp, () => ForEachWindowOfActive(h => Native.ShowWindow(h, Native.SW_MINIMIZE))));
        m.Items.Add(Item("Mostrar no Explorador", hasApp, () =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_activePath}\"") { UseShellExecute = true })));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Encerrar " + AppName.Text, hasApp, () => ForEachWindowOfActive(h => Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))));
        m.IsOpen = true;
    }

    void ShowWindowMenu()
    {
        var m = new ContextMenu { PlacementTarget = WindowBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        IntPtr h = _activeWindow;
        bool ok = h != IntPtr.Zero && Native.IsWindow(h);
        m.Items.Add(Item("Minimizar", ok, () => Native.ShowWindow(h, Native.SW_MINIMIZE)));
        m.Items.Add(Item("Maximizar / Restaurar", ok, () =>
        {
            const int SW_MAXIMIZE = 3;
            bool max = (Native.GetWindowLongPtr(h, -16).ToInt64() & 0x01000000L) != 0; // WS_MAXIMIZE
            Native.ShowWindow(h, max ? Native.SW_RESTORE : SW_MAXIMIZE);
            Native.SetForegroundWindow(h);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Fechar janela", ok, () => Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)));
        m.IsOpen = true;
    }

    void ForEachWindowOfActive(Action<IntPtr> act)
    {
        uint target = _activePid;
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsAppWindow(h))
            {
                Native.GetWindowThreadProcessId(h, out uint p);
                if (Native.ResolvePid(h, p, Native.GetProcessPath(p)) == target) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        foreach (var h in list) act(h);
    }

    static MenuItem Item(string text, bool enabled, Action click)
    {
        var mi = new MenuItem { Header = text, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }
}
