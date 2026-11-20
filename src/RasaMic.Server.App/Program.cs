using Avalonia;
using Velopack;

namespace RasaMic.Server.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Install/update hooks (shortcuts, registry, first-run). Exits the
        // process when invoked as a Velopack hook; a no-op otherwise.
        VelopackApp.Build().Run();
        _ = AutoUpdate.CheckAsync();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}