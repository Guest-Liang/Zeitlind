using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Protobuf;

namespace Zeitlind.Protocol.Identity;

public static class GenshinPlayerIdentityDecoder
{
    private const uint MinimumNineDigitUid = 100_000_000;
    private const uint MaximumPlausibleUid = 999_999_999;
    private const int MaximumSmallPacketBody = 256;

    public static bool TryDecode(CapturedPacket packet, out uint uid, out string detail)
    {
        ArgumentNullException.ThrowIfNull(packet);
        uid = 0;
        detail = string.Empty;

        if (
            packet.Body.Length is 0 or > MaximumSmallPacketBody
            || !ProtoWire.TryParse(packet.Body, out var message)
            || message is null
        )
        {
            return false;
        }

        if (!TryFindUniqueUid(message, out uid, out var fieldNumber, out var depth))
        {
            uid = 0;
            return false;
        }

        detail = $"命令 {packet.CommandId}，字段 {fieldNumber}，嵌套 {depth}";
        return true;
    }

    private static bool TryFindUniqueUid(ProtoMessage message, out uint uid, out uint fieldNumber, out int depth)
    {
        uid = 0;
        fieldNumber = 0;
        depth = 0;
        if (TryFindUniqueUid(message, MinimumNineDigitUid, MaximumPlausibleUid, out uid, out fieldNumber))
        {
            return true;
        }

        foreach (var field in message.Fields)
        {
            if (
                field.WireType != ProtoWireType.LengthDelimited
                || !ProtoWire.TryParse(field.Bytes, out var child)
                || child is null
            )
            {
                continue;
            }

            if (
                !TryFindUniqueUid(
                    child,
                    MinimumNineDigitUid,
                    MaximumPlausibleUid,
                    out var nestedUid,
                    out var nestedField
                )
            )
            {
                continue;
            }

            if (uid != 0 && uid != nestedUid)
            {
                uid = 0;
                fieldNumber = 0;
                return false;
            }

            uid = nestedUid;
            fieldNumber = nestedField;
            depth = 1;
        }

        return uid != 0;
    }

    private static bool TryFindUniqueUid(
        ProtoMessage message,
        uint minimum,
        uint maximum,
        out uint uid,
        out uint fieldNumber
    )
    {
        uid = 0;
        fieldNumber = 0;
        foreach (var field in message.Fields)
        {
            if (field.WireType != ProtoWireType.Varint || field.Varint < minimum || field.Varint > maximum)
            {
                continue;
            }

            if (uid != 0)
            {
                uid = 0;
                fieldNumber = 0;
                return false;
            }

            uid = (uint)field.Varint;
            fieldNumber = field.Number;
        }

        return uid != 0;
    }
}
