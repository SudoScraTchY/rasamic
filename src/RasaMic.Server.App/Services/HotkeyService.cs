using SharpHook;
using SharpHook.Data;

namespace RasaMic.Server.App.Services;

/// <summary>Global mute hotkey via SharpHook; works from fullscreen apps.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SimpleGlobalHook _hook = new();
    private readonly HashSet<KeyCode> _pressed = new();
    private readonly TaskCompletionSource _enabled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Hotkey _hotkey;
    private bool _started;

    public HotkeyService(Hotkey? hotkey = null) => _hotkey = hotkey ?? Hotkey.Default;

    public event Action? MuteHotkey;

    /// <summary>While true, the next modified key press is reported instead of triggering mute.</summary>
    public bool CaptureMode { get; set; }

    public event Action<Hotkey>? HotkeyCaptured;

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _hook.HookEnabled += (_, _) => _enabled.TrySetResult();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
        _hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
    }

    private static bool IsModifier(KeyCode key) => key is
        KeyCode.VcLeftControl or KeyCode.VcRightControl or
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt or
        KeyCode.VcLeftShift or KeyCode.VcRightShift;

    private bool CtrlDown => _pressed.Contains(KeyCode.VcLeftControl) || _pressed.Contains(KeyCode.VcRightControl);
    private bool AltDown => _pressed.Contains(KeyCode.VcLeftAlt) || _pressed.Contains(KeyCode.VcRightAlt);
    private bool ShiftDown => _pressed.Contains(KeyCode.VcLeftShift) || _pressed.Contains(KeyCode.VcRightShift);

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        _pressed.Add(e.Data.KeyCode);
        if (IsModifier(e.Data.KeyCode))
            return;

        if (CaptureMode)
        {
            if (CtrlDown || AltDown || ShiftDown)
            {
                CaptureMode = false;
                HotkeyCaptured?.Invoke(new Hotkey(CtrlDown, AltDown, ShiftDown, e.Data.KeyCode));
            }
            return;
        }

        if (e.Data.KeyCode == _hotkey.Key &&
            CtrlDown == _hotkey.Ctrl && AltDown == _hotkey.Alt && ShiftDown == _hotkey.Shift)
        {
            MuteHotkey?.Invoke();
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e) => _pressed.Remove(e.Data.KeyCode);

    public void Dispose()
    {
        _hook.KeyPressed -= OnKeyPressed;
        _hook.KeyReleased -= OnKeyReleased;
        // Stopping while the background install thread is still running fails in libuiohook.
        if (_started)
            _enabled.Task.Wait(500);
        try { _hook.Dispose(); }
        catch (HookException) { /* handlers are detached; a stuck hook dies with the process */ }
    }
}