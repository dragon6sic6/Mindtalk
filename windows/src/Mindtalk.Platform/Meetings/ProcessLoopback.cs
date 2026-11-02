using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Mindtalk.Platform.Meetings;

/// <summary>
/// The sound one app plays — and every process it started (Teams' web views, a browser's tabs) —
/// through Windows' process loopback (Windows 10 2004 and later). Only that app's sound: not
/// music, not notifications. The Mac's Core Audio process tap. NAudio doesn't wrap this, so the
/// activation is done here.
/// </summary>
internal static class ProcessLoopback
{
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private const ushort VtBlob = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0;
    /// AUTOCONVERTPCM | SRC_DEFAULT_QUALITY: Windows converts to the format asked for.
    private const AudioClientStreamFlags Convert = (AudioClientStreamFlags)0x88000000;

    /// <summary>Opens a capture stream of <paramref name="processId"/>'s sound (and its children's).</summary>
    public static StreamCapture Open(int processId, Action<float[], float> data, Action<Exception?> stopped)
    {
        var client = Activate(processId);
        // Windows converts to whatever is asked: 16 kHz mono float is what's needed.
        foreach (var format in new[] { WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), new WaveFormat(44_100, 16, 2) })
        {
            try { return new StreamCapture(client, format, AudioClientStreamFlags.Loopback | Convert, "Mindtalk call sound", data, stopped); }
            catch (COMException) { client.Dispose(); client = Activate(processId); }
        }
        client.Dispose();
        throw new NotSupportedException("Process loopback refused every format.");
    }

    private static AudioClient Activate(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType, { TargetProcessId, ProcessLoopbackMode } }
        var parameters = Marshal.AllocHGlobal(12);
        // PROPVARIANT holding a BLOB: vt at 0, cbSize at 8, pBlobData at 16 (64-bit) / 12 (32-bit).
        var variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(parameters, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(parameters, 4, processId);
            Marshal.WriteInt32(parameters, 8, IncludeTargetProcessTree);
            for (var i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
            Marshal.WriteInt16(variant, 0, (short)VtBlob);
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, IntPtr.Size == 8 ? 16 : 12, parameters);

            var handler = new Completion();
            ActivateAudioInterfaceAsync(VirtualDevice, AudioClientId, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Process loopback didn't activate.");
            handler.Operation!.GetActivateResult(out var result, out var activated);
            Marshal.ThrowExceptionForHR(result);
            return new AudioClient((IAudioClient)activated);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateCompletionHandler completionHandler, out IActivateOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateCompletionHandler
    {
        void ActivateCompleted(IActivateOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// Marks the handler as callable from any thread, which the activation requires.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    private sealed class Completion : IActivateCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public IActivateOperation? Operation;

        public void ActivateCompleted(IActivateOperation activateOperation)
        {
            Operation = activateOperation;
            Done.Set();
        }
    }
}
