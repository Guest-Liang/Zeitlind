using System.Runtime.InteropServices;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Genshin;

internal static unsafe class PacketHook
{
    /// <summary>
    /// 解析器定位方式、10+2 帧布局和解压调用点的版本。语义改变时递增。
    /// </summary>
    public const int LocatorVersion = 1;

    private const string ModuleName = "YuanShen.exe";
    private const uint HeadMagic = 0x4567;
    private const uint TailMagic = 0x89AB;

    private static readonly PacketHookState HookState = new();
    private static readonly HookFrameTransport CaptureTransport = FrameTransport.Transport;

    private static delegate* unmanaged<nint, nint, int, nint, byte, int> _original;
    private static GenshinPacketDecompress _decompress;

    public static PacketHookInstallation WaitForModuleAndInstall(TimeSpan timeout)
    {
        var located = PacketHookLocator.WaitForParser(timeout, HeadMagic, TailMagic, ModuleName);
        _decompress = GenshinPacketDecompress.Locate(located.ModuleBase, located.Parser);
        HookState.Install(
            located.Parser,
            (nint)(delegate* unmanaged<nint, nint, int, nint, byte, int>)&Detour,
            static trampoline => _original = (delegate* unmanaged<nint, nint, int, nint, byte, int>)trampoline
        );
        return new PacketHookInstallation(located.ModuleBase, located.Parser.Rva);
    }

    public static void Uninstall()
    {
        HookState.Uninstall();
    }

    [UnmanagedCallersOnly]
    private static int Detour(nint parser, nint arraySlot, int availableLength, nint context, byte alternateDecrypt)
    {
        var result = _original(parser, arraySlot, availableLength, context, alternateDecrypt);
        if (result == 1)
        {
            try
            {
                Capture(arraySlot, availableLength);
            }
            catch
            {
                // 不能让导出器的异常影响到游戏
            }
        }

        return result;
    }

    private static void Capture(nint arraySlot, int availableLength)
    {
        if (arraySlot == 0)
        {
            return;
        }

        var managedArray = *(nint*)arraySlot;
        if (!GenshinPacketFrameParser.TryParse(managedArray, availableLength, out var frame))
        {
            return;
        }

        if (!GenshinPacketFrameParser.TryGetDecompressedSize(frame.Header, out var unzipLength))
        {
            _ = CaptureTransport.TryEnqueuePacket(frame.CommandId, frame.Header, frame.Body);
            return;
        }

        if (_decompress.TryDecompress(frame.Body, unzipLength, out var body))
        {
            _ = CaptureTransport.TryEnqueuePacket(frame.CommandId, frame.Header, body);
        }
    }
}
