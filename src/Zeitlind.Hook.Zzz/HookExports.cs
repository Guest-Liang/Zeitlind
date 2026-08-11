using System.Runtime.InteropServices;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Zzz;

public static class HookExports
{
    private static readonly HookHost Host = new(
        FrameTransport.Transport,
        PacketHook.WaitForModuleAndInstall,
        Run,
        PacketHook.Uninstall
    );

    [UnmanagedCallersOnly(EntryPoint = "ZeitlindZzzHookMain")]
    public static int Start(nint bootstrapContext)
    {
        return Host.Start(bootstrapContext);
    }

    private static void Run(PacketHookInstallation installation)
    {
        var uidLocation = CurrentUidLocator.Locate(installation.ModuleBase);
        var uidReader = new CurrentUidReader(uidLocation);
        FrameTransport.SendReady(installation.ParserRva, uidLocation);
        FrameTransport.Pump(uidReader);
    }
}
