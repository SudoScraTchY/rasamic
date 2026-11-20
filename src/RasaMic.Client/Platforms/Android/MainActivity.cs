using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace RasaMic.Client;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // Headless connect for adb smoke / screen-locked runs:
    // am start -n dev.rasamic.client/... --es rasa_ip 192.168.1.5
    public static string? AutoConnectIp;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Read before base.OnCreate: MAUI creates and appears MainPage inside it.
        AutoConnectIp = Intent?.GetStringExtra("rasa_ip");
        base.OnCreate(savedInstanceState);
        ApplyBarColors();
        if (AutoConnectIp is string ip)
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(4000);
                var app = Microsoft.Maui.Controls.Application.Current;
                if (app is null || app.Windows.Count == 0)
                    return;
                var page = app.Windows[0].Page;
                if (page is Microsoft.Maui.Controls.Shell sh)
                    page = sh.CurrentPage;
                if (page is MainPage mp)
                    mp.ConnectWithIp(ip);
            });
        }
        // Android 13+ requires a runtime grant before the foreground-service notification shows.
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 0);
        }

        StartBenchIfRequested(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.GetStringExtra("rasa_ip") is string ip)
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(500);
                var app = Microsoft.Maui.Controls.Application.Current;
                if (app is null || app.Windows.Count == 0)
                    return;
                var page = app.Windows[0].Page;
                if (page is Microsoft.Maui.Controls.Shell sh)
                    page = sh.CurrentPage;
                if (page is MainPage mp)
                    mp.ConnectWithIp(ip);
            });
        }
        StartBenchIfRequested(intent);
    }

    // adb shell am start -n dev.rasamic.client/... --ei rasa_bench 1
    // Delivered via OnNewIntent while a session is live, this measures the
    // encode path with capture active (production HAL perf-lock conditions).
    private void StartBenchIfRequested(Intent? intent)
    {
        if (intent?.GetIntExtra("rasa_bench", 0) != 1)
            return;
        var thread = new System.Threading.Thread(() =>
        {
            try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio); } catch { }
            Android.OS.PerformanceHintManager.Session? hint = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                try
                {
                    var phm = (Android.OS.PerformanceHintManager?)Android.App.Application.Context
                        .GetSystemService(Android.Content.Context.PerformanceHintService);
                    hint = phm?.CreateHintSession(new[] { Android.OS.Process.MyTid() }, 10_000_000);
                }
                catch { }
            }
            Android.Util.Log.Info("RasaBench",
                $"boost: urgentAudio priority, hint={(hint is not null)}, capture-active={MicForegroundService.IsRunning}");
            foreach (var (channels, bitrate, label) in new[] { (1, 32000, "mono32k"), (2, 256000, "stereo256k") })
            {
                using var pipeline = new RasaMic.Audio.AudioPipeline(
                    new RasaMic.Audio.PipelineOptions { Channels = channels, Bitrate = bitrate });
                var pcm = new short[480 * channels];
                for (int i = 0; i < pcm.Length; i++)
                    pcm[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 48000));
                var pkt = new byte[2048];
                for (int i = 0; i < 200; i++)
                {
                    pipeline.Feed(pcm);
                    while (pipeline.ReadPacket(pkt, out _, out _) > 0) { }
                }
                long totalNs = 0;
                const int n = 2000;
                for (int i = 0; i < n; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    pipeline.Feed(pcm);
                    while (pipeline.ReadPacket(pkt, out _, out _) > 0) { }
                    long durNs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000_000 / System.Diagnostics.Stopwatch.Frequency;
                    totalNs += durNs;
                    hint?.ReportActualWorkDuration(durNs);
                }
                Android.Util.Log.Info("RasaBench",
                    $"{label} feed+drain mean {totalNs / 1000.0 / n:F1} us on {Build.Model}");
            }
            hint?.Dispose();
        }) { IsBackground = true, Name = "rasa-bench" };
        thread.Start();
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyBarColors();
    }

    // System bars match the page background in both themes (UiMode is in ConfigurationChanges,
    // so the activity is not recreated on theme switch).
    private void ApplyBarColors()
    {
        bool night = (Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes;
        var bar = Android.Graphics.Color.ParseColor(night ? "#171512" : "#F5F3EE");
        if (Window is Android.Views.Window w)
        {
            w.AddFlags(Android.Views.WindowManagerFlags.DrawsSystemBarBackgrounds);
            w.ClearFlags(Android.Views.WindowManagerFlags.TranslucentStatus | Android.Views.WindowManagerFlags.TranslucentNavigation);
            w.SetStatusBarColor(bar);
            w.SetNavigationBarColor(bar);
            // MIUI/MAUI may re-apply theme bar colors after window setup; re-assert once laid out.
            w.DecorView.Post(() =>
            {
                w.SetStatusBarColor(bar);
                w.SetNavigationBarColor(bar);
            });
        }
        if (Window?.DecorView is Android.Views.View dv)
        {
            var flags = dv.SystemUiFlags;
            if (night)
                flags &= ~(Android.Views.SystemUiFlags.LightStatusBar | Android.Views.SystemUiFlags.LightNavigationBar);
            else
                flags |= Android.Views.SystemUiFlags.LightStatusBar | Android.Views.SystemUiFlags.LightNavigationBar;
            dv.SystemUiFlags = flags;
        }
    }
}