using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Hsr;

internal static class FrameTransport
{
    internal static readonly HookFrameTransport Transport = new();

    public static void SendReady(uint parserRva)
    {
        Transport.SendReady(parserRva, PacketHook.LocatorVersion);
    }

    public static void Pump()
    {
        while (!Transport.IsShutdownRequested)
        {
            while (Transport.TryDequeue(out var message))
            {
                Transport.Send(message);
            }

            Transport.WaitForActivity(100);
        }
    }
}
