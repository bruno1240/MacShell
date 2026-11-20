namespace MacShell;

/// <summary>
/// Área de trabalho limpa: esconde a camada de ícones enquanto o MacShell roda.
/// Não altera a opção "Mostrar ícones da área de trabalho" do Windows; ao sair, os ícones voltam.
/// </summary>
internal static class DesktopIcons
{
    public static bool Hidden { get; private set; }

    public static void Hide()
    {
        Hidden = true;
        var lv = FindIconList();
        if (lv != IntPtr.Zero) Native.ShowWindow(lv, Native.SW_HIDE);
    }

    public static void Show()
    {
        Hidden = false;
        var lv = FindIconList();
        if (lv != IntPtr.Zero) Native.ShowWindow(lv, Native.SW_SHOWNA);
    }

    /// <summary>Se o Explorer reiniciar, a lista de ícones é recriada visível: esconde de novo.</summary>
    public static void Enforce()
    {
        if (!Hidden) return;
        var lv = FindIconList();
        if (lv != IntPtr.Zero && Native.IsWindowVisible(lv)) Native.ShowWindow(lv, Native.SW_HIDE);
    }

    /// <summary>Progman (ou um WorkerW) → SHELLDLL_DefView → SysListView32 "FolderView".</summary>
    static IntPtr FindIconList()
    {
        IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr worker = IntPtr.Zero;
            while ((worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                defView = Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) break;
            }
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }
}
