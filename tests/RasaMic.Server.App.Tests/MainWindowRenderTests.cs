using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using RasaMic.Server.App.ViewModels;
using Xunit;

namespace RasaMic.Server.App.Tests;

public class MainWindowRenderTests
{
    private static string ShotDir =>
        Environment.GetEnvironmentVariable("RASA_GUI_SHOTS") ?? AppContext.BaseDirectory;

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Bitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
        frame.Save(Path.Combine(ShotDir, $"gui-{name}.png"));
    }

    [AvaloniaFact]
    public void MainWindow_Renders_BothThemes()
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            var window = new MainWindow { Width = 560, Height = 1000 };
            window.Show();
            Capture(window, variant == ThemeVariant.Light ? "light" : "dark");
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task MainWindow_Renders_LiveServer_BothThemes()
    {
        using var vm = new MainWindowViewModel(() => new MainWindowTests.NullBackend());
        vm.Port = 0;
        await vm.StartCommand.ExecuteAsync(null);
        try
        {
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                var window = new MainWindow(vm) { Width = 560, Height = 1650 };
                window.Show();
                Capture(window, variant == ThemeVariant.Light ? "live-light" : "live-dark");
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            await vm.StopCommand.ExecuteAsync(null);
        }
    }
}