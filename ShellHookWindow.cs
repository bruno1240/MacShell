using System.Runtime.InteropServices;

namespace MacShell;

/// <summary>
/// Janela invisível que recebe os mesmos avisos que a barra de tarefas do Windows
/// (janela criada/fechada/ativada e, principalmente, "piscar pedindo atenção").
/// Criada direto pela API do Windows: a janela do dock (WPF, transparente) não recebe esses avisos.
/// </summary>
internal sealed class ShellHookWindow : IDisposable
{
    readonly Native.WndProc _proc; // mantém o delegate vivo
    readonly uint _shellMsg;
    readonly Action<int, IntPtr> _onEvent;
    public IntPtr Handle { get; }

    public ShellHookWindow(Action<int, IntPtr> onEvent)
    {
        _onEvent = onEvent;
        _shellMsg = Native.RegisterWindowMessage("SHELLHOOK");
        _proc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "MacShellShellHook",
        };
        Native.RegisterClassEx(ref wc);
        Handle = Native.CreateWindowEx(0x80 /* TOOLWINDOW */, "MacShellShellHook", "MacShell Avisos", 0x80000000 /* WS_POPUP, invisível */,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (Handle != IntPtr.Zero) Native.RegisterShellHookWindow(Handle);
    }

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _shellMsg)
        {
            try { _onEvent((int)(wParam.ToInt64() & 0xFFFF), lParam); } catch { }
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.DeregisterShellHookWindow(Handle);
            Native.DestroyWindow(Handle);
        }
    }
}
