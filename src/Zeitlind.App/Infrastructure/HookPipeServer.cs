using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Zeitlind.Core.Interop;
using Zeitlind.Protocol.Capture;

namespace Zeitlind.App.Infrastructure;

internal abstract record HookMessage;

internal sealed record HookReadyMessage(
    ulong ParserRva,
    int ParserLocatorVersion,
    ulong UidRootSlotRva,
    int UidLocatorVersion,
    int EquivalentUidPathCount
) : HookMessage;

internal sealed record HookPacketMessage(CapturedPacket Packet, uint Uid) : HookMessage;

internal sealed record HookErrorMessage(string Error) : HookMessage;

internal sealed record HookUidMessage(uint Uid) : HookMessage;

internal sealed class HookPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;

    public HookPipeServer(int processId)
    {
        var pipeName = HookProtocol.GetPipeName(processId);
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        ApplicationLog.WriteDebug($"已创建 Hook 命名管道：{pipeName}", writeToConsole: false);
    }

    public Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        return _pipe.WaitForConnectionAsync(cancellationToken);
    }

    public async Task<HookMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await _pipe.ReadExactlyAsync(lengthBuffer, cancellationToken);
        var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (messageLength is < 1 or > HookProtocol.MaximumMessageLength)
        {
            throw new InvalidDataException($"Hook 消息长度 {messageLength} 无效");
        }

        var message = GC.AllocateUninitializedArray<byte>(messageLength);
        await _pipe.ReadExactlyAsync(message, cancellationToken);

        return message[0] switch
        {
            HookProtocol.ReadyMessage => ParseReady(message),
            HookProtocol.PacketMessage => ParsePacket(message),
            HookProtocol.ErrorMessage => ParseError(message),
            HookProtocol.UidMessage => ParseUid(message),
            _ => throw new InvalidDataException($"Hook 消息类型 {message[0]} 未知"),
        };
    }

    public ValueTask DisposeAsync()
    {
        return _pipe.DisposeAsync();
    }

    private static HookReadyMessage ParseReady(ReadOnlySpan<byte> message)
    {
        if (message.Length != HookProtocol.ReadyMessageLength)
        {
            throw new InvalidDataException("Hook 就绪消息长度无效");
        }

        return new HookReadyMessage(
            BinaryPrimitives.ReadUInt64LittleEndian(message[HookProtocol.ReadyParserRvaOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[HookProtocol.ReadyParserLocatorVersionOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(message[HookProtocol.ReadyUidRootSlotRvaOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[HookProtocol.ReadyUidLocatorVersionOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[HookProtocol.ReadyEquivalentUidPathCountOffset..])
        );
    }

    private static HookPacketMessage ParsePacket(ReadOnlySpan<byte> message)
    {
        if (message.Length < HookProtocol.PacketPrefixLength)
        {
            throw new InvalidDataException("Hook 数据包消息过短");
        }

        var uid = BinaryPrimitives.ReadUInt32LittleEndian(message[HookProtocol.PacketUidOffset..]);
        var commandId = BinaryPrimitives.ReadUInt16LittleEndian(message[HookProtocol.PacketCommandIdOffset..]);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(message[HookProtocol.PacketHeaderLengthOffset..]);
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(message[HookProtocol.PacketBodyLengthOffset..]);
        if (
            headerLength < 0
            || bodyLength < 0
            || headerLength > HookProtocol.MaximumPacketHeaderLength
            || bodyLength > HookProtocol.MaximumPacketBodyLength
            || (long)headerLength + bodyLength != message.Length - HookProtocol.PacketPrefixLength
        )
        {
            throw new InvalidDataException("Hook 数据包中的头部或正文长度无效");
        }

        var header = message.Slice(HookProtocol.PacketPrefixLength, headerLength).ToArray();
        var body = message.Slice(HookProtocol.PacketPrefixLength + headerLength, bodyLength).ToArray();

        return new HookPacketMessage(
            new CapturedPacket
            {
                CommandId = commandId,
                Header = header,
                Body = body,
                CapturedAt = DateTimeOffset.UtcNow,
            },
            uid
        );
    }

    private static HookErrorMessage ParseError(ReadOnlySpan<byte> message)
    {
        if (message.Length < HookProtocol.ErrorPrefixLength)
        {
            throw new InvalidDataException("Hook 错误消息过短");
        }

        var textLength = BinaryPrimitives.ReadInt32LittleEndian(message[HookProtocol.ErrorTextLengthOffset..]);
        if (textLength < 0 || textLength != message.Length - HookProtocol.ErrorPrefixLength)
        {
            throw new InvalidDataException("Hook 错误消息文本长度无效");
        }

        return new HookErrorMessage(Encoding.UTF8.GetString(message[HookProtocol.ErrorPrefixLength..]));
    }

    private static HookUidMessage ParseUid(ReadOnlySpan<byte> message)
    {
        if (message.Length != HookProtocol.UidMessageLength)
        {
            throw new InvalidDataException("Hook UID 消息长度无效");
        }

        return new HookUidMessage(BinaryPrimitives.ReadUInt32LittleEndian(message[HookProtocol.UidValueOffset..]));
    }
}
