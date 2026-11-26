using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace RasaMic.Client;

// Keeps capture + sender alive when the app is backgrounded or the screen is off.
[Service(ForegroundServiceType = ForegroundService.TypeMicrophone, Exported = false)]
public class MicForegroundService : Service
{
    private const string ChannelId = "rasa-mic";
    private const int NotificationId = 1;
    private PowerManager.WakeLock? _wakeLock;

    public static volatile bool IsRunning;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(MicForegroundService)));

    public static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(MicForegroundService)));

    public override void OnCreate()
    {
        base.OnCreate();
        var channel = new NotificationChannel(ChannelId, "Microphone streaming", NotificationImportance.Low)
        {
            Description = "Shown while Rasa streams your microphone to a server.",
        };
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Rasa")
            .SetContentText("Microphone streaming active")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        else
            StartForeground(NotificationId, notification);
        IsRunning = true;

        if (_wakeLock is null)
        {
            var pm = (PowerManager)GetSystemService(PowerService)!;
            _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "rasa:mic");
            _wakeLock?.Acquire();
        }
        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        IsRunning = false;
        _wakeLock?.Release();
        _wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}