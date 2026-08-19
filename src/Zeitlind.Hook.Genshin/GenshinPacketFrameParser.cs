using Zeitlind.Core.Interop;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Genshin;

internal readonly ref struct GenshinPacketFrame
{
    public GenshinPacketFrame(ushort commandId, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        CommandId = commandId;
        Header = header;
        Body = body;
    }

    public ushort CommandId { get; }

    public ReadOnlySpan<byte> Header { get; }

    public ReadOnlySpan<byte> Body { get; }
}

internal static unsafe class GenshinPacketFrameParser
{
    internal const ushort HeadMagic = 0x4567;
    internal const ushort TailMagic = 0x89AB;

    private const int MinimumPacketLength = 12;
    private const int PacketPrefixLength = 10;
    private const int PacketSuffixLength = 2;

    public static bool TryParse(nint managedArray, int availableLength, out GenshinPacketFrame frame)
    {
        frame = default;
        if (managedArray == 0 || availableLength < MinimumPacketLength)
        {
            return false;
        }

        var arrayLength = *(nuint*)(managedArray + 0x18);
        if (arrayLength < MinimumPacketLength)
        {
            return false;
        }

        var packet = (byte*)managedArray + 0x20;
        if (ReadBigEndianUInt16(packet) != HeadMagic)
        {
            return false;
        }

        var commandId = ReadBigEndianUInt16(packet + 2);
        var headerLength = ReadBigEndianUInt16(packet + 4);
        var bodyLength = ReadBigEndianUInt32(packet + 6);
        if (bodyLength > HookProtocol.MaximumPacketBodyLength)
        {
            return false;
        }

        var totalLength = (ulong)PacketPrefixLength + headerLength + bodyLength + PacketSuffixLength;
        if (totalLength > (ulong)availableLength || totalLength > arrayLength)
        {
            return false;
        }

        var header = new ReadOnlySpan<byte>(packet + PacketPrefixLength, headerLength);
        var body = new ReadOnlySpan<byte>(packet + PacketPrefixLength + headerLength, checked((int)bodyLength));
        var tail = packet + PacketPrefixLength + headerLength + bodyLength;
        if (ReadBigEndianUInt16(tail) != TailMagic)
        {
            return false;
        }

        frame = new GenshinPacketFrame(commandId, header, body);
        return true;
    }

    public static bool TryGetDecompressedSize(ReadOnlySpan<byte> header, out uint unzipLength)
    {
        unzipLength = 0;
        var offset = 0;
        while (offset < header.Length)
        {
            if (!TryReadVarint(header, ref offset, out var tag) || tag == 0)
            {
                return false;
            }

            if (tag == 64)
            {
                if (!TryReadVarint(header, ref offset, out var length) || length == 0 || length > uint.MaxValue)
                {
                    return false;
                }

                unzipLength = (uint)length;
                return true;
            }

            switch (tag & 7)
            {
                case 0:
                    if (!TryReadVarint(header, ref offset, out _))
                    {
                        return false;
                    }

                    break;
                case 1:
                    if (!TryAdvance(header, ref offset, sizeof(ulong)))
                    {
                        return false;
                    }

                    break;
                case 2:
                    if (
                        !TryReadVarint(header, ref offset, out var len)
                        || len > int.MaxValue
                        || !TryAdvance(header, ref offset, (int)len)
                    )
                    {
                        return false;
                    }

                    break;
                case 5:
                    if (!TryAdvance(header, ref offset, sizeof(uint)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            if (offset > header.Length)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryAdvance(ReadOnlySpan<byte> span, ref int offset, int count)
    {
        if ((uint)offset > (uint)span.Length || count < 0 || count > span.Length - offset)
        {
            return false;
        }

        offset += count;
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> span, ref int offset, out ulong value)
    {
        value = 0;
        for (var i = 0; i < 8; i++)
        {
            if (offset >= span.Length)
            {
                return false;
            }

            var b = span[offset++];
            value |= (ulong)(b & 0x7F) << (i * 7);
            if (b < 0x80)
            {
                return true;
            }
        }

        return false;
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
