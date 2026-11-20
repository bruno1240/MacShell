using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacShell;

/// <summary>
/// Animação numa thread própria, cadenciada na taxa de atualização do monitor (60, 144, 240 Hz...)
/// com um temporizador de alta precisão. Não disputa tempo com a interface e não depende do DWM
/// para liberar o próximo quadro (o que segurava a animação em ~25 quadros/s).
/// </summary>
internal sealed class FrameLoop
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    static readonly double FramePeriodMs = DetectPeriod();
    int _generation;

    static double DetectPeriod()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        int hz = GetDeviceCaps(dc, 116 /* VREFRESH */);
        ReleaseDC(IntPtr.Zero, dc);
        if (hz < 30 || hz > 500) hz = 60;
        return 1000.0 / hz;
    }

    /// <param name="seconds">duração</param>
    /// <param name="frame">recebe o progresso 0..1 (roda fora da thread da interface)</param>
    /// <param name="done">roda na thread da interface quando termina (não roda se for cancelada)</param>
    public void Run(double seconds, Action<double> frame, Dispatcher ui, Action? done = null)
    {
        int gen = Interlocked.Increment(ref _generation);
        var thread = new Thread(() =>
        {
            timeBeginPeriod(1); // sleep com precisão de 1 ms só enquanto anima
            try
            {
                var sw = Stopwatch.StartNew();
                double next = 0;
                while (true)
                {
                    if (gen != Volatile.Read(ref _generation)) return; // outra animação assumiu
                    double p = Math.Min(1, sw.Elapsed.TotalSeconds / seconds);
                    frame(p);
                    if (p >= 1) break;

                    next += FramePeriodMs;
                    double now = sw.Elapsed.TotalMilliseconds;
                    if (next < now) next = now; // atrasou: não acumula
                    double wait = next - now;
                    if (wait > 1.5) Thread.Sleep((int)(wait - 1));
                    while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50); // último ~1 ms: precisão
                }
                if (done != null && gen == Volatile.Read(ref _generation)) ui.BeginInvoke(done);
            }
            finally { timeEndPeriod(1); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "MacShell-anim",
        };
        thread.Start();
    }

    public void Cancel() => Interlocked.Increment(ref _generation);
}
