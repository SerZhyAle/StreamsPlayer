using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0223 requirement 8: Windows' process-loopback capture, asked for "every process except this
/// application's own tree", so a broadcast this application receives and plays is never captured and sent
/// on. The audio engine offers it as a virtual device that only the asynchronous activation call can open;
/// this class is that call and nothing else - the stream is initialized and read by
/// <see cref="WindowsAudioCapture"/> exactly like an ordinary loopback client.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsProcessLoopback
{
    // The first build the documentation names for the exclude-one-process-tree mode.
    private const int MinimumBuild = 20348;
    private const string VirtualDevicePath = "VAD\\Process_Loopback";
    private const ushort VariantTypeBlob = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int ModeExcludeTargetProcessTree = 1;
    private const int ActivationTimeoutMilliseconds = 5000;
    private const int HresultTimeout = unchecked((int)0x800705B4);
    private const int HresultNoInterface = unchecked((int)0x80004002);

    /// <summary>Whether this Windows build can exclude a process tree; asks the OS, never opens a device.</summary>
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);

    /// <summary>
    /// Opens an audio client that captures everything the PC plays except this process and its children.
    /// Returns 0 and the client as a plain COM object for the caller to cast to <c>IAudioClient</c>, or the
    /// failing HRESULT. Blocks the calling thread until Windows completes the activation, so it belongs on
    /// the capture thread, never on the interface thread.
    /// </summary>
    internal static int ActivateExcludingOwnProcessTree(Guid audioClientInterfaceId, out object? audioClient)
    {
        audioClient = null;
        var parameters = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)Environment.ProcessId,
            LoopbackMode = ModeExcludeTargetProcessTree
        };
        var parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        try
        {
            Marshal.StructureToPtr(parameters, parametersPointer, false);
            var variant = new BlobPropVariant
            {
                VariantType = VariantTypeBlob,
                BlobSize = (uint)Marshal.SizeOf<ActivationParams>(),
                BlobData = parametersPointer
            };
            var handler = new CompletionHandler();
            var started = ActivateAudioInterfaceAsync(VirtualDevicePath, ref audioClientInterfaceId, ref variant,
                handler, out IActivateAudioInterfaceAsyncOperation? operation);
            if (started != 0)
            {
                return started;
            }

            try
            {
                if (!handler.Completed.Wait(ActivationTimeoutMilliseconds))
                {
                    return HresultTimeout;
                }

                if (handler.Result != 0)
                {
                    return handler.Result;
                }

                audioClient = handler.Activated;
                return audioClient is null ? HresultNoInterface : 0;
            }
            finally
            {
                if (operation is not null)
                {
                    Marshal.ReleaseComObject(operation);
                }

                GC.KeepAlive(handler);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(parametersPointer);
        }
    }

    // AUDIOCLIENT_ACTIVATION_PARAMS with its process-loopback member: type, process id, mode.
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    // A PROPVARIANT holding VT_BLOB; the sequential layout puts the pointer where the union keeps it on
    // both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    // Windows calls this back on one of its own threads; it must be agile (IAgileObject) or the activation
    // call refuses it. The result is read inside the callback, where the operation is documented as valid.
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal ManualResetEventSlim Completed { get; } = new(false);
        internal int Result { get; private set; }
        internal object? Activated { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Result = activateOperation.GetActivateResult(out int activateResult, out object? activated);
            if (Result == 0)
            {
                Result = activateResult;
                Activated = activated;
            }

            Completed.Set();
            return 0;
        }
    }

    [DllImport("mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid interfaceId,
        ref BlobPropVariant activationParameters,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation? activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    // Marker interface with no methods: it only tells COM that the callback may run on any thread.
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }
}
