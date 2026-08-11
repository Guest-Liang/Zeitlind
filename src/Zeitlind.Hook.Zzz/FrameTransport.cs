using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Zzz;

internal static class FrameTransport
{
    private const int UidPollIntervalMilliseconds = 100;
    private const int StableUidObservationCount = 2;

    internal static readonly HookFrameTransport Transport = new();

    public static void SendReady(uint parserRva, CurrentUidLocation uidLocation)
    {
        Transport.SendReady(
            parserRva,
            PacketHook.LocatorVersion,
            uidLocation.RootSlotRva,
            CurrentUidLocator.LocatorVersion,
            uidLocation.EquivalentPathCount
        );
    }

    public static void Pump(CurrentUidReader uidReader)
    {
        uint candidateUid = 0;
        uint publishedUid = 0;
        var stableObservations = 0;
        long nextUidPoll = 0;

        while (!Transport.IsShutdownRequested)
        {
            var now = Environment.TickCount64;
            if (now >= nextUidPoll)
            {
                PollUid(uidReader, ref candidateUid, ref publishedUid, ref stableObservations);
                nextUidPoll = now + UidPollIntervalMilliseconds;
            }

            while (Transport.TryDequeue(out var message))
            {
                _ = HookFrameTransport.TrySetPacketUid(message, publishedUid);
                Transport.Send(message);
            }

            Transport.WaitForActivity(UidPollIntervalMilliseconds);
        }
    }

    private static void PollUid(
        CurrentUidReader uidReader,
        ref uint candidateUid,
        ref uint publishedUid,
        ref int stableObservations
    )
    {
        if (!uidReader.TryRead(out var observedUid))
        {
            candidateUid = 0;
            stableObservations = 0;
            if (publishedUid != 0)
            {
                publishedUid = 0;
                Transport.SendUid(0);
            }

            return;
        }

        if (candidateUid == observedUid)
        {
            stableObservations = Math.Min(stableObservations + 1, StableUidObservationCount);
        }
        else
        {
            candidateUid = observedUid;
            stableObservations = 1;
        }

        if (stableObservations >= StableUidObservationCount && publishedUid != candidateUid)
        {
            publishedUid = candidateUid;
            Transport.SendUid(publishedUid);
        }
    }
}
