namespace MacShell;

/// <summary>
/// Esconde a barra de tarefas do Windows e só a mostra enquanto o Iniciar (tecla Windows) ou a busca estão abertos.
/// Não altera nenhuma configuração do sistema: apenas esconde/mostra a janela da barra enquanto o MacShell roda.
/// Ao sair (ou se o MacShell falhar), a barra volta ao normal.
/// </summary>
internal static class TaskbarController
{
    static readonly HashSet<string> ShowWith = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
    };

    public static bool Enabled { get; private set; }
    static bool _shown;

    public static void Enable()
    {
        Enabled = true;
        _shown = false;
        SetVisible(false);
    }

    public static void Disable()
    {
        Enabled = false;
        SetVisible(true);
    }

    /// <summary>Chamado a cada troca de janela em foco.</summary>
    public static void OnForeground(IntPtr h)
    {
        if (!Enabled || h == IntPtr.Zero) return;
        // Ao minimizar a última janela, o Windows dá o foco à própria barra: isso não deve mostrá-la.
        // Ela só fica visível pela barra se já estava aberta por causa do Iniciar.
        bool want = IsStartOrSearch(h) || (_shown && IsTray(h));
        if (want != _shown) { _shown = want; SetVisible(want); }
        if (!want) EnforceSoon();
    }

    /// <summary>
    /// O Explorer reexibe a barra sozinho em alguns momentos (ao minimizar uma janela, por exemplo).
    /// Confere de novo logo depois, em poucos instantes.
    /// </summary>
    public static void EnforceSoon()
    {
        if (!Enabled) return;
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null) return;
        foreach (int ms in new[] { 60, 250, 700 })
            _ = ui.InvokeAsync(async () => { await Task.Delay(ms); Enforce(); });
    }

    /// <summary>Chamado periodicamente: se o Explorer reiniciar e a barra reaparecer, esconde de novo.</summary>
    public static void Enforce()
    {
        if (!Enabled) return;
        IntPtr fg = Native.GetForegroundWindow();
        _shown = IsStartOrSearch(fg) || (_shown && IsTray(fg));
        if (!_shown)
            foreach (var t in Trays())
                if (Native.IsWindowVisible(t)) Native.ShowWindow(t, Native.SW_HIDE);
    }

    static void SetVisible(bool visible)
    {
        foreach (var t in Trays()) Native.ShowWindow(t, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
    }

    static IEnumerable<IntPtr> Trays()
    {
        IntPtr main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr sec = IntPtr.Zero;
        while ((sec = Native.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return sec;
    }

    static bool IsTray(IntPtr h)
    {
        string c = Native.GetClass(h);
        return c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    static bool IsStartOrSearch(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        string? path = Native.GetProcessPath(pid);
        return path != null && ShowWith.Contains(Path.GetFileName(path));
    }
}
