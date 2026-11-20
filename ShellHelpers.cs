using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacShell;

// ---------------------------------------------------------------- ícones em alta resolução
internal static class IconLoader
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path, int size = 128)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            // 0x1 BIGGERSIZEOK | 0x4 ICONONLY
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out IntPtr hbm) == 0 && hbm != IntPtr.Zero)
            {
                try { img = FromHBitmap(hbm); } finally { Native.DeleteObject(hbm); }
            }
            Marshal.ReleaseComObject(factory);
        }
        catch { /* ícone indisponível */ }
        Cache[path] = img;
        return img;
    }

    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bmp) == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var px = new byte[w * h * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try { if (Native.GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0) == 0) return null; }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }
}

// ---------------------------------------------------------------- atalhos .lnk
internal static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }

    public static string? ResolveTarget(string lnk)
    {
        object? o = null;
        try
        {
            o = new CShellLink();
            ((IPersistFile)o).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)o).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var s = Environment.ExpandEnvironmentVariables(sb.ToString());
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
        finally { if (o != null) Marshal.ReleaseComObject(o); }
    }
}

// ---------------------------------------------------------------- reserva de espaço na tela (AppBar)
internal sealed class AppBar
{
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    public const uint ABE_TOP = 1, ABE_BOTTOM = 3;

    readonly Window _w;
    readonly uint _edge;
    readonly double _thicknessDip;
    IntPtr _h;
    uint _msg;
    bool _registered;

    public event Action<bool>? FullscreenChanged;
    public Native.RECT Rect { get; private set; }

    public AppBar(Window w, uint edge, double thicknessDip)
    {
        _w = w; _edge = edge; _thicknessDip = thicknessDip;
    }

    public void Register()
    {
        _h = new WindowInteropHelper(_w).Handle;
        _msg = Native.RegisterWindowMessage("MacShellAppBar_" + _edge);
        HwndSource.FromHwnd(_h).AddHook(Hook);
        var abd = New();
        abd.uCallbackMessage = _msg;
        Native.SHAppBarMessage(ABM_NEW, ref abd);
        _registered = true;
        SetPos();
    }

    public void Remove()
    {
        if (!_registered) return;
        var abd = New();
        Native.SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }

    Native.APPBARDATA New() => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _h, uEdge = _edge };

    public void SetPos()
    {
        double scale = VisualTreeHelper.GetDpi(_w).DpiScaleY;
        int thick = (int)Math.Round(_thicknessDip * scale);
        int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
        var abd = New();
        abd.rc = new Native.RECT { Left = 0, Right = sw, Top = 0, Bottom = sh };
        if (_edge == ABE_TOP) abd.rc.Bottom = thick; else abd.rc.Top = sh - thick;
        Native.SHAppBarMessage(ABM_QUERYPOS, ref abd);
        if (_edge == ABE_TOP) abd.rc.Bottom = abd.rc.Top + thick; else abd.rc.Top = abd.rc.Bottom - thick;
        Native.SHAppBarMessage(ABM_SETPOS, ref abd);
        Rect = abd.rc;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_registered && msg == (int)_msg)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED: SetPos(); break;
                case ABN_FULLSCREENAPP: FullscreenChanged?.Invoke(lParam != IntPtr.Zero); break;
            }
        }
        return IntPtr.Zero;
    }
}

// ---------------------------------------------------------------- tema claro/escuro do Windows
internal static class Theme
{
    public static bool IsLight
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
    }

    public static event Action? Changed;

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        };
    }

    public static SolidColorBrush Brush(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}

// ---------------------------------------------------------------- nomes amigáveis de apps
internal static class AppInfo
{
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public static string FriendlyName(string path)
    {
        if (Names.TryGetValue(path, out var n)) return n;
        string file = Path.GetFileNameWithoutExtension(path);
        if (file.Equals("explorer", StringComparison.OrdinalIgnoreCase)) n = "Explorador de Arquivos";
        else
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                n = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription!
                  : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName! : file;
            }
            catch { n = file; }
        }
        n = n.Trim();
        if (n.Length > 40) n = file;
        Names[path] = n;
        return n;
    }
}

// ---------------------------------------------------------------- apps fixados no dock
internal sealed class PinnedApp
{
    public string Name { get; set; } = "";
    public string Launch { get; set; } = "";
    public string? Target { get; set; }

    /// <summary>Atalho (.lnk/.url), programa (.exe), pasta ou qualquer arquivo arrastado para o dock.</summary>
    public static PinnedApp? FromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Directory.Exists(path))
            return new PinnedApp { Name = new DirectoryInfo(path).Name, Launch = path, Target = path };
        if (!File.Exists(path)) return null;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string bare = Path.GetFileNameWithoutExtension(path);
        return ext switch
        {
            ".lnk" => new PinnedApp { Name = bare, Launch = path, Target = ShellLink.ResolveTarget(path) },
            ".exe" => new PinnedApp { Name = AppInfo.FriendlyName(path), Launch = path, Target = path },
            ".url" => new PinnedApp { Name = bare, Launch = path, Target = null },
            _ => new PinnedApp { Name = Path.GetFileName(path), Launch = path, Target = path },
        };
    }
}

internal static class DockConfig
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell");
    static readonly string FilePath = Path.Combine(Dir, "dock.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static List<PinnedApp> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(FilePath)) ?? new();
                var clean = Clean(loaded);
                if (clean.Count != loaded.Count) Save(clean);
                return clean;
            }
        }
        catch { }
        var list = Defaults();
        Save(list);
        return list;
    }

    public static void Save(List<PinnedApp> list)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
    }

    /// <summary>Começa com o Explorador e os mesmos apps fixados na barra de tarefas do Windows.</summary>
    static List<PinnedApp> Defaults()
    {
        var list = new List<PinnedApp>
        {
            new() { Name = "Explorador de Arquivos", Launch = ExplorerPath, Target = ExplorerPath },
        };
        string pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (Directory.Exists(pinned))
        {
            foreach (var lnk in Directory.GetFiles(pinned, "*.lnk").OrderBy(f => f))
            {
                var target = ShellLink.ResolveTarget(lnk);
                if (target != null && target.Equals(ExplorerPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (target == null && IsExplorerName(Path.GetFileNameWithoutExtension(lnk))) continue; // atalho do Explorador (sem arquivo)
                list.Add(new PinnedApp { Name = Path.GetFileNameWithoutExtension(lnk), Launch = lnk, Target = target });
            }
        }
        return list;
    }

    static bool IsExplorerName(string n) =>
        n.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) || n.Equals("Explorador de Arquivos", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remove entradas duplicadas do Explorador de configurações antigas.</summary>
    public static List<PinnedApp> Clean(List<PinnedApp> list)
    {
        bool hasExplorer = false;
        var result = new List<PinnedApp>();
        foreach (var p in list)
        {
            bool isExplorer = (p.Target ?? "").Equals(ExplorerPath, StringComparison.OrdinalIgnoreCase)
                              || (p.Target == null && IsExplorerName(p.Name));
            if (isExplorer) { if (hasExplorer) continue; hasExplorer = true; }
            result.Add(p);
        }
        return result;
    }
}
