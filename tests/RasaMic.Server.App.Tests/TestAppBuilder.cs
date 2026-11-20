using Avalonia;
using Avalonia.Headless;
using RasaMic.Server.App;

[assembly: AvaloniaTestApplication(typeof(RasaMic.Server.App.Tests.TestAppBuilder))]
// Global hotkey hooks and the headless UI thread are process-wide; parallel classes race them.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace RasaMic.Server.App.Tests;

public static class TestAppBuilder
{
    // Real Skia drawing: the headless font stub cannot resolve embedded font families.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}