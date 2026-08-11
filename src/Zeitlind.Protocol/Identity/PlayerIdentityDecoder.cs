using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Protobuf;

namespace Zeitlind.Protocol.Identity;

public static class PlayerIdentityDecoder
{
    public const uint PlayerGetTokenScRspCommandId = 81;
    private const uint LegacyPlayerGetTokenScRspCommandId = 91;
    private const uint UidFieldNumber = 15;
    private const uint MinimumPlausibleUid = 100_000_000;
    private const uint MaximumPlausibleUid = 999_999_999;

    public static bool TryDecode(CapturedPacket packet, out uint uid)
    {
        return TryDecode(packet, out uid, out _);
    }

    public static bool TryDecode(CapturedPacket packet, out uint uid, out uint fieldNumber)
    {
        ArgumentNullException.ThrowIfNull(packet);
        uid = 0;
        fieldNumber = 0;

        if (
            packet.CommandId is not PlayerGetTokenScRspCommandId and not LegacyPlayerGetTokenScRspCommandId
            || !ProtoWire.TryParse(packet.Body, out var message)
            || message is null
        )
        {
            return false;
        }

        var found = false;
        foreach (var field in message.Fields)
        {
            if (field.Number != UidFieldNumber || field.WireType != ProtoWireType.Varint)
            {
                continue;
            }

            if (field.Varint is < MinimumPlausibleUid or > MaximumPlausibleUid)
            {
                continue;
            }

            if (found)
            {
                uid = 0;
                fieldNumber = 0;
                return false;
            }

            uid = (uint)field.Varint;
            fieldNumber = field.Number;
            found = true;
        }

        if (found)
        {
            return true;
        }

        // 命令 ID 足够稳定，可以用来识别登录响应；而 protobuf 的字段编号
        // 在不同的游戏版本之间可能会被混淆。如果字段 15 的位置发生变化，
        // 那么只接受该响应中唯一一个明确的九位数 varint 值。
        foreach (var field in message.Fields)
        {
            if (
                field.WireType != ProtoWireType.Varint
                || field.Varint is < MinimumPlausibleUid or > MaximumPlausibleUid
            )
            {
                continue;
            }

            if (found)
            {
                uid = 0;
                fieldNumber = 0;
                return false;
            }

            uid = (uint)field.Varint;
            fieldNumber = field.Number;
            found = true;
        }

        return found;
    }
}
