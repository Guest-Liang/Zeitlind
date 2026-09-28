namespace Zeitlind.Hook.Common;

public sealed class PlainPacketCapture
{
    private readonly HookFrameTransport transport;
    private readonly uint headMagic;
    private readonly uint tailMagic;

    public PlainPacketCapture(HookFrameTransport transport, uint headMagic, uint tailMagic)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.headMagic = headMagic;
        this.tailMagic = tailMagic;
    }

    public bool TryEnqueue(nint managedArray, uint offset, int availableLength)
    {
        return PlainPacketFrameParser.TryParse(
                managedArray,
                offset,
                availableLength,
                headMagic,
                tailMagic,
                out var frame
            ) && transport.TryEnqueuePacket(frame.CommandId, frame.Header, frame.Body);
    }
}
