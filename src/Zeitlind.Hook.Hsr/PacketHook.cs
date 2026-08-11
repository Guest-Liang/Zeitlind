using System.Runtime.InteropServices;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Hsr;

internal static unsafe class PacketHook
{
    /// <summary>
    /// 解析器定位方式和被 Hook 函数 ABI 的版本。语义改变时递增。
    /// </summary>
    public const int LocatorVersion = 1;

    private const uint HeadMagic = 0x9D74_C714;
    private const uint TailMagic = 0xD7A1_52C8;

    private static readonly PacketHookState HookState = new();
    private static readonly PlainPacketCapture CaptureTransport = new(FrameTransport.Transport, HeadMagic, TailMagic);

    private static delegate* unmanaged<nint, nint, nint, int> _original;

    public static PacketHookInstallation WaitForModuleAndInstall(TimeSpan timeout)
    {
        return HookState.WaitForModuleAndInstall(
            timeout,
            HeadMagic,
            TailMagic,
            (nint)(delegate* unmanaged<nint, nint, nint, int>)&Detour,
            static trampoline => _original = (delegate* unmanaged<nint, nint, nint, int>)trampoline
        );
    }

    public static void Uninstall()
    {
        HookState.Uninstall();
    }

    [UnmanagedCallersOnly]
    private static int Detour(nint parser, nint bufferView, nint xorKey)
    {
        var result = _original(parser, bufferView, xorKey);

        if (result == 1)
        {
            try
            {
                Capture(bufferView);
            }
            catch
            {
                // Never let exporter failures escape into the game.
            }
        }

        return result;
    }

    private static void Capture(nint bufferView)
    {
        if (bufferView == 0)
        {
            return;
        }

        var managedArray = *(nint*)bufferView;
        var offset = *(uint*)(bufferView + 8);
        var availableLength = *(int*)(bufferView + 12);
        _ = CaptureTransport.TryEnqueue(managedArray, offset, availableLength);
    }
}
