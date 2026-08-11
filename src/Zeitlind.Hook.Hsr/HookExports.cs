using System.Runtime.InteropServices;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Hsr;

public static class HookExports
{
    private static readonly HookHost Host = new(
        FrameTransport.Transport,
        PacketHook.WaitForModuleAndInstall,
        Run,
        PacketHook.Uninstall
    );

    [UnmanagedCallersOnly(EntryPoint = "ZeitlindHsrHookMain")]
    public static int Start(nint bootstrapContext)
    {
        return Host.Start(bootstrapContext);
    }

    private static void Run(PacketHookInstallation installation)
    {
        FrameTransport.SendReady(installation.ParserRva);
        FrameTransport.Pump();
    }
}
