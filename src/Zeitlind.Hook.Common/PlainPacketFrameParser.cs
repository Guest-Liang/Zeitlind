using Zeitlind.Core.Interop;

namespace Zeitlind.Hook.Common;

public readonly ref struct PlainPacketFrame
{
    public PlainPacketFrame(ushort commandId, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        CommandId = commandId;
        Header = header;
        Body = body;
    }

    public ushort CommandId { get; }

    public ReadOnlySpan<byte> Header { get; }

    public ReadOnlySpan<byte> Body { get; }
}

public static unsafe class PlainPacketFrameParser
{
    private const int MinimumPacketLength = 16;
    private const int PacketPrefixLength = 12;
    private const int PacketSuffixLength = 4;
    public static bool TryParse(
        nint managedArray,
        uint offset,
        int availableLength,
        uint headMagic,
        uint tailMagic,
        out PlainPacketFrame frame
    )
    {
        frame = default;
        if (managedArray == 0 || availableLength < MinimumPacketLength)
        {
            return false;
        }

        var arrayLength = *(nuint*)(managedArray + 0x18);
        if ((nuint)offset >= arrayLength)
        {
            return false;
        }

        var remainingArrayLength = arrayLength - offset;
        if (remainingArrayLength < MinimumPacketLength)
        {
            return false;
        }

        var packet = (byte*)managedArray + 0x20 + offset;
        if (ReadBigEndianUInt32(packet) != headMagic)
        {
            return false;
        }

        var commandId = ReadBigEndianUInt16(packet + 4);
        var headerLength = ReadBigEndianUInt16(packet + 6);
        var bodyLength = ReadBigEndianUInt32(packet + 8);
        if (bodyLength > HookProtocol.MaximumPacketBodyLength)
        {
            return false;
        }

        var totalLength = (ulong)PacketPrefixLength + headerLength + bodyLength + PacketSuffixLength;
        if (totalLength > (ulong)availableLength || totalLength > remainingArrayLength)
        {
            return false;
        }

        var header = new ReadOnlySpan<byte>(packet + PacketPrefixLength, headerLength);
        var body = new ReadOnlySpan<byte>(packet + PacketPrefixLength + headerLength, checked((int)bodyLength));
        var tail = packet + PacketPrefixLength + headerLength + bodyLength;
        if (ReadBigEndianUInt32(tail) != tailMagic)
        {
            return false;
        }

        frame = new PlainPacketFrame(commandId, header, body);
        return true;
    }

    private static ushort ReadBigEndianUInt16(byte* value)
    {
        return (ushort)((value[0] << 8) | value[1]);
    }

    private static uint ReadBigEndianUInt32(byte* value)
    {
        return (uint)(value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3]);
    }
}
