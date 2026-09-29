using System.Runtime.CompilerServices;

namespace Glossa.App;

/// <summary>
/// Glossa.exe's entry. «--resume-guard &lt;pid&gt;» starts only the small process that keeps a paused game safe
/// (Games.ResumeGuardHost), without WPF or any window; everything else starts the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--resume-guard" && int.TryParse(args[1], out var owner))
            return Games.ResumeGuardHost.Run(owner);
        return RunApp();
    }

    // Kept apart so the guard never loads WPF: the JIT resolves App only when this method is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
