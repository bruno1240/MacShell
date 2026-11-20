using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacShell;

public partial class App : Application
{
    Mutex? _single;
    TopBarWindow? _bar;
    DockWindow? _dock;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "MacShell_SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        // Renderização por GPU, sem animações de fundo: CPU parada quando ninguém mexe no dock.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(120));

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            TaskbarController.Disable(); // nunca deixar a barra de tarefas escondida se algo falhar
            DesktopIcons.Show();
            Log(ex.ExceptionObject as Exception);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { TaskbarController.Disable(); DesktopIcons.Show(); };

        // Garante que o MacShell continua na inicialização do Windows (outro programa pode ter removido).
        EnsureAutostart();
        SessionEnding += (_, _) => EnsureAutostart(); // confere de novo ao desligar/reiniciar

        base.OnStartup(e);
        _bar = new TopBarWindow();
        _dock = new DockWindow();
        _dock.FullscreenChanged += full => _bar.SetFullscreen(full);
        _bar.Show();
        _dock.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bar?.Cleanup();
        _dock?.Cleanup();
        _single?.Dispose();
        base.OnExit(e);
    }

    public static void Quit() => Current.Shutdown();

    static void EnsureAutostart()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return;
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            string want = $"\"{exe}\"";
            if (run != null && !string.Equals(run.GetValue("MacShell") as string, want, StringComparison.OrdinalIgnoreCase))
                run.SetValue("MacShell", want);
            Directory.CreateDirectory(DockConfig.Dir);
            File.AppendAllText(Path.Combine(DockConfig.Dir, "startup.log"), $"[{DateTime.Now:s}] iniciado ({exe})\n");
        }
        catch (Exception ex) { Log(ex); }
    }

    static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(DockConfig.Dir);
            File.AppendAllText(Path.Combine(DockConfig.Dir, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch { }
    }
}
