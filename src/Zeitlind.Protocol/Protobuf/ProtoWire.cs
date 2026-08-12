namespace Zeitlind.Protocol.Protobuf;

internal enum ProtoWireType : byte
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    Fixed32 = 5,
}

internal readonly record struct ProtoField(
    uint Number,
    ProtoWireType WireType,
    ulong Varint,
    ReadOnlyMemory<byte> Bytes
);

internal sealed class ProtoMessage
{
    public ProtoMessage(List<ProtoField> fields)
    {
        Fields = fields;
    }

    public IReadOnlyList<ProtoField> Fields { get; }
}

internal static class ProtoWire
{
    private const int MaximumFieldsPerMessage = 65_536;
    private const int MaximumPackedVarintCount = 65_536;

    public static bool TryParse(ReadOnlyMemory<byte> data, out ProtoMessage? message)
    {
        message = null;
        if (data.IsEmpty)
        {
            return false;
        }

        var span = data.Span;
        var offset = 0;
        var fields = new List<ProtoField>();

        while (offset < span.Length)
        {
            if (
                fields.Count >= MaximumFieldsPerMessage
                || !TryReadVarint(span, ref offset, out var rawTag)
                || rawTag > uint.MaxValue
            )
            {
                return false;
            }

            var fieldNumber = (uint)(rawTag >> 3);
            var rawWireType = (byte)(rawTag & 7);
            if (fieldNumber == 0 || fieldNumber > 0x1FFF_FFFF)
            {
                return false;
            }

            switch (rawWireType)
            {
                case (byte)ProtoWireType.Varint:
                    if (!TryReadVarint(span, ref offset, out var value))
                    {
                        return false;
                    }

                    fields.Add(new ProtoField(fieldNumber, ProtoWireType.Varint, value, ReadOnlyMemory<byte>.Empty));
                    break;

                case (byte)ProtoWireType.Fixed64:
                    if (!TryAdvance(span, ref offset, sizeof(ulong)))
                    {
                        return false;
                    }

                    fields.Add(new ProtoField(fieldNumber, ProtoWireType.Fixed64, 0, ReadOnlyMemory<byte>.Empty));
                    break;

                case (byte)ProtoWireType.LengthDelimited:
                    if (!TryReadVarint(span, ref offset, out var rawLength) || rawLength > int.MaxValue)
                    {
                        return false;
                    }

                    var length = (int)rawLength;
                    if (length < 0 || offset > span.Length - length)
                    {
                        return false;
                    }

                    var bytes = data.Slice(offset, length);
                    offset += length;
                    fields.Add(new ProtoField(fieldNumber, ProtoWireType.LengthDelimited, 0, bytes));
                    break;

                case (byte)ProtoWireType.Fixed32:
                    if (!TryAdvance(span, ref offset, sizeof(uint)))
                    {
                        return false;
                    }

                    fields.Add(new ProtoField(fieldNumber, ProtoWireType.Fixed32, 0, ReadOnlyMemory<byte>.Empty));
                    break;

                default:
                    return false;
            }
        }

        if (fields.Count == 0)
        {
            return false;
        }

        message = new ProtoMessage(fields);
        return true;
    }

    public static bool TryParsePackedVarints(ReadOnlyMemory<byte> data, out ulong[] values)
    {
        values = [];
        if (data.IsEmpty)
        {
            return false;
        }

        var span = data.Span;
        var offset = 0;
        var parsed = new List<ulong>();
        while (offset < span.Length)
        {
            if (
                parsed.Count >= MaximumPackedVarintCount
                || !TryReadVarint(span, ref offset, out var value)
            )
            {
                return false;
            }

            parsed.Add(value);
        }

        values = parsed.ToArray();
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> span, ref int offset, out ulong value)
    {
        value = 0;

        for (var index = 0; index < 10; index++)
        {
            if ((uint)offset >= (uint)span.Length)
            {
                return false;
            }

            var current = span[offset++];
            if (index == 9 && current > 1)
            {
                return false;
            }

            value |= (ulong)(current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAdvance(ReadOnlySpan<byte> span, ref int offset, int count)
    {
        if (offset > span.Length - count)
        {
            return false;
        }

        offset += count;
        return true;
    }
}
