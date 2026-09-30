using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0101 / SP-0121: recording on the LibVLC engine. LibVLC 3 has no public call that toggles recording on a
/// playing media, so this sets the input thread's <c>record</c> variable through three symbols that are not part
/// of its public API (X-01). Decision 2 of SP-0121 keeps that mechanism behind <see cref="Probe"/>: Record is
/// offered only when the engine is a 3.0 build and every symbol resolves, so a native package that changes them
/// costs the Record button, not the process.
/// <para>Every instance method must be called under the owning backend's media gate.</para>
/// <para>Where the file goes: the engine writes into <c>input-record-path</c>, which each open sets per leg to a
/// directory of its own inside this engine's private staging directory. The instance-wide value, the engine
/// directory itself, stays as the fallback, and the finisher looks there only when the leg directory is empty.</para>
/// </summary>
internal sealed class LibVlcRecording
{
    private const string VlcLibrary = "libvlc";
    private const string CoreLibrary = "libvlccore";

    private static readonly object ProbeGate = new();
    private static string? _probeFailure;
    private static bool _probed;

    private readonly CurrentLog _log;
    private string? _legDirectory;
    private RecordingSegment? _segment;

    internal LibVlcRecording(CurrentLog log)
    {
        _log = log;
        InstanceDirectory = RecordingStaging.NewInstanceDirectory();
    }

    /// <summary>This engine's private staging directory; the instance-wide <c>--input-record-path</c>.</summary>
    internal string InstanceDirectory { get; }

    /// <summary>
    /// SP-0164: whether the engine is writing, from its own staging - a file of the segment's own past the
    /// grace window. A start LibVLC accepted and later refused keeps no file, and this is the flag that says
    /// so. Safe without the gate: a reference read and a directory listing, a stale answer corrected next tick.
    /// </summary>
    internal bool IsWriting(DateTimeOffset now) =>
        _segment is { } segment
        && RecordingWriteProbe.IsWriting(segment.StartedAt, segment.StagingDirectories, segment.PreexistingFiles, now);

    /// <summary>
    /// Null when this LibVLC can record; otherwise what is missing, for the log. Checked once per process: the
    /// native libraries cannot change under a running process.
    /// </summary>
    internal static string? Probe(string? engineVersion)
    {
        lock (ProbeGate)
        {
            if (!_probed)
            {
                _probeFailure = RunProbe(engineVersion);
                _probed = true;
            }

            return _probeFailure;
        }
    }

    /// <summary>Gives the media about to be opened its own recording directory. Not created until a recording starts.</summary>
    internal void PrepareLeg(Media media, int leg)
    {
        _legDirectory = Path.Combine(InstanceDirectory, $"leg-{leg:D4}");
        media.AddOption($":input-record-path={_legDirectory}");
    }

    internal bool Start(MediaPlayer player, RecordingTarget target)
    {
        if (_segment is not null)
        {
            return true;
        }

        var legDirectory = _legDirectory ?? InstanceDirectory;
        Directory.CreateDirectory(InstanceDirectory);
        Directory.CreateDirectory(legDirectory);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        existing.UnionWith(Directory.GetFiles(InstanceDirectory));
        existing.UnionWith(Directory.GetFiles(legDirectory));

        if (!SetRecord(player, true))
        {
            RecordingStaging.RemoveIfEmpty(legDirectory);
            return false;
        }

        _segment = new RecordingSegment
        {
            Engine = "libvlc",
            Target = target,
            StartedAt = DateTimeOffset.Now,
            StagingDirectories = legDirectory == InstanceDirectory ? [InstanceDirectory] : [legDirectory, InstanceDirectory],
            PreexistingFiles = existing
        };
        _log.Event("RECORD START", "engine=libvlc", "ok=true", $"staging={legDirectory}");
        return true;
    }

    /// <summary>
    /// Ends the current segment and hands it back. <paramref name="player"/> is the engine to ask to stop writing,
    /// or null when the caller is about to close the media anyway (a re-open, a stop, teardown) - closing the input
    /// closes the file, which is what <paramref name="player"/> being null records on the segment.
    /// </summary>
    internal RecordingSegment? End(MediaPlayer? player, string reason)
    {
        if (_segment is not { } segment)
        {
            return null;
        }

        _segment = null;
        if (player is not null)
        {
            SetRecord(player, false);
        }

        segment.EndedAt = DateTimeOffset.Now;
        segment.ClosedByEngine = player is null;
        _log.Event("RECORD SEGMENT END", "engine=libvlc", $"reason={reason}", $"length_s={(int)segment.Length.TotalSeconds}");
        return segment;
    }

    private bool SetRecord(MediaPlayer player, bool on)
    {
        var input = libvlc_get_input_thread(player.NativeReference);
        if (input == IntPtr.Zero)
        {
            _log.Event("RECORD NATIVE", $"on={on}", "ok=false", "reason=no_input");
            return false;
        }

        try
        {
            var result = var_Set(input, "record", new VlcValue { b_bool = on });
            if (result != 0)
            {
                _log.Event("RECORD NATIVE", $"on={on}", "ok=false", $"code={result}");
            }

            return result == 0;
        }
        finally
        {
            vlc_object_release(input);
        }
    }

    private static string? RunProbe(string? engineVersion)
    {
        // vlc_value_t, the record variable and the input-thread accessor are LibVLC 3's; 4.x removed the accessor
        // and reshaped the object model, so anything but a 3.0 build is refused before a symbol is looked up.
        if (engineVersion is null || !engineVersion.StartsWith("3.0.", StringComparison.Ordinal))
        {
            return $"version={engineVersion ?? "unknown"}";
        }

        var assembly = typeof(LibVlcRecording).Assembly;
        if (!HasExport(VlcLibrary, "libvlc_get_input_thread", assembly))
        {
            return "missing=libvlc_get_input_thread";
        }

        if (!HasExport(CoreLibrary, "var_Set", assembly))
        {
            return "missing=var_Set";
        }

        return HasExport(CoreLibrary, "vlc_object_release", assembly) ? null : "missing=vlc_object_release";
    }

    // Resolved the way the DllImports below resolve, so a success here is the binding they will get.
    private static bool HasExport(string library, string symbol, Assembly assembly) =>
        NativeLibrary.TryLoad(library, assembly, null, out var handle)
        && NativeLibrary.TryGetExport(handle, symbol, out _);

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct VlcValue
    {
        [FieldOffset(0)] public long i_int;
        [FieldOffset(0)] public bool b_bool;
        [FieldOffset(0)] public IntPtr psz_string;
    }

    [DllImport(VlcLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libvlc_get_input_thread(IntPtr p_mi);

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vlc_object_release(IntPtr p_obj);

    [DllImport(CoreLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int var_Set(IntPtr p_obj, [MarshalAs(UnmanagedType.LPStr)] string psz_name, VlcValue val);
}
