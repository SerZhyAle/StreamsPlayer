using System.Runtime.InteropServices;

namespace StreamsPlayer.App;

/// <summary>
/// Holds a Windows power request while a stream is actively playing so the machine's idle-sleep
/// timer does not cut a long listening or viewing session short (SP-0027). Only the idle-sleep
/// timer is suppressed via <c>ES_CONTINUOUS</c>; explicit user sleep, hibernate, lid-close, and
/// the power button are never overridden.
///
/// Requests are reference-counted with separate system and display counters because audio and one
/// or more player windows can play at once — releasing one session must not let the machine sleep
/// while another still plays. Audio holds a system-only request (the screen may turn off); video
/// holds system + display (the user is watching). The <see cref="Enabled"/> flag mirrors the
/// persisted preference and gates whether counted requests translate into an actual OS call, so
/// toggling the option releases or re-acquires an active lock immediately.
///
/// All members must be called on the WPF UI thread: <c>SetThreadExecutionState</c> is thread-affine
/// and every caller (playback start/stop, settings, exit) runs on that single thread.
/// </summary>
internal static class WakeGuard
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private static bool _enabled = true;
    private static int _systemHolds;
    private static int _displayHolds;

    /// <summary>Mirrors <c>CatalogState.KeepAwakeDuringPlayback</c>; recomputes the OS state on change.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Apply();
        }
    }

    /// <summary>
    /// Registers an active playback session. Dispose the returned handle when the session ends to
    /// release its request. <paramref name="keepDisplayOn"/> is true for video/RTSP (keep the
    /// display awake too) and false for audio (let the display turn off normally).
    /// </summary>
    public static IDisposable Acquire(bool keepDisplayOn)
    {
        _systemHolds++;
        if (keepDisplayOn)
        {
            _displayHolds++;
        }

        Apply();
        return new Handle(keepDisplayOn);
    }

    /// <summary>Final safety net on app exit: clears any residual wake request outright.</summary>
    public static void Reset()
    {
        _systemHolds = 0;
        _displayHolds = 0;
        SetThreadExecutionState(ExecutionState.Continuous);
    }

    private static void Release(bool keepDisplayOn)
    {
        _systemHolds = Math.Max(0, _systemHolds - 1);
        if (keepDisplayOn)
        {
            _displayHolds = Math.Max(0, _displayHolds - 1);
        }

        Apply();
    }

    private static void Apply()
    {
        var flags = ExecutionState.Continuous;
        if (_enabled && _systemHolds > 0)
        {
            flags |= ExecutionState.SystemRequired;
            if (_displayHolds > 0)
            {
                flags |= ExecutionState.DisplayRequired;
            }
        }

        SetThreadExecutionState(flags);
    }

    private sealed class Handle(bool keepDisplayOn) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Release(keepDisplayOn);
        }
    }
}
