using System.Runtime.InteropServices;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Zzz;

internal static unsafe class PacketHook
{
    /// <summary>
    /// 解析器定位方式的版本。定位规则或被 Hook 函数的语义改变时递增。
    /// </summary>
    public const int LocatorVersion = 2;

    private const uint HeadMagic = 0x0123_4567;
    private const uint TailMagic = 0x89AB_CDEF;

    private static readonly PacketHookState HookState = new();
    private static readonly PlainPacketCapture CaptureTransport = new(FrameTransport.Transport, HeadMagic, TailMagic);

    private static delegate* unmanaged<nint, nint, uint, int, byte, int> _original;

    public static PacketHookInstallation WaitForModuleAndInstall(TimeSpan timeout)
    {
        return HookState.WaitForModuleAndInstall(
            timeout,
            HeadMagic,
            TailMagic,
            (nint)(delegate* unmanaged<nint, nint, uint, int, byte, int>)&Detour,
            static trampoline => _original = (delegate* unmanaged<nint, nint, uint, int, byte, int>)trampoline
        );
    }

    public static void Uninstall()
    {
        HookState.Uninstall();
    }

    [UnmanagedCallersOnly]
    private static int Detour(nint parser, nint managedArray, uint offset, int availableLength, byte alternateDecrypt)
    {
        var result = _original(parser, managedArray, offset, availableLength, alternateDecrypt);

        if (result == 1)
        {
            try
            {
                _ = CaptureTransport.TryEnqueue(managedArray, offset, availableLength);
            }
            catch
            {
                // 不能让导出器的异常影响到游戏
            }
        }

        return result;
    }
}
