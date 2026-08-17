namespace Zeitlind.Protocol.Achievements;

internal static class VarintFieldReader
{
    public static uint? ReadUInt32(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber, bool defaultWhenMissing)
    {
        if (fieldNumber is null)
        {
            return null;
        }

        if (!row.TryGetValue(fieldNumber.Value, out var value))
        {
            return defaultWhenMissing ? 0U : null;
        }

        return value <= uint.MaxValue ? (uint)value : null;
    }

    public static ulong? ReadUInt64(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber, bool defaultWhenMissing)
    {
        if (fieldNumber is null)
        {
            return null;
        }

        return row.TryGetValue(fieldNumber.Value, out var value) ? value
            : defaultWhenMissing ? 0UL
            : null;
    }

    public static long? ReadInt64(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber)
    {
        return fieldNumber is not null && row.TryGetValue(fieldNumber.Value, out var value) && value <= long.MaxValue
            ? (long)value
            : null;
    }

    public static bool? ReadBoolean(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber)
    {
        return fieldNumber is null ? null : ReadBoolean(row, fieldNumber.Value);
    }

    public static bool? ReadBoolean(IReadOnlyDictionary<uint, ulong> row, uint fieldNumber)
    {
        return row.TryGetValue(fieldNumber, out var value) && value <= 1 ? value == 1 : null;
    }
}
