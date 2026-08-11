using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Zeitlind.Core.Interop;

namespace Zeitlind.Hook.Common;

public sealed class HookFrameTransport
{
    private const int MaximumQueuedBytes = 64 * 1024 * 1024;

    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly AutoResetEvent _queueChanged = new(false);

    private NamedPipeClientStream? _pipe;
    private int _queuedBytes;
    private int _shutdown;
    private int _connected;

    public bool IsShutdownRequested => Volatile.Read(ref _shutdown) != 0;

    public void Connect(int processId)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            HookProtocol.GetPipeName(processId),
            PipeDirection.Out,
            PipeOptions.None
        );
        pipe.Connect(30_000);
        _pipe = pipe;
        Volatile.Write(ref _connected, 1);
    }

    public bool TryEnqueuePacket(ushort commandId, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        if (Volatile.Read(ref _connected) == 0 || IsShutdownRequested)
        {
            return false;
        }

        var messageLength = checked(HookProtocol.PacketPrefixLength + header.Length + body.Length);
        if (messageLength > HookProtocol.MaximumMessageLength)
        {
            return false;
        }

        var queued = Interlocked.Add(ref _queuedBytes, messageLength);
        if (queued > MaximumQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -messageLength);
            return false;
        }

        var message = GC.AllocateUninitializedArray<byte>(messageLength);
        var span = message.AsSpan();
        span[0] = HookProtocol.PacketMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(span[HookProtocol.PacketUidOffset..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[HookProtocol.PacketCommandIdOffset..], commandId);
        BinaryPrimitives.WriteInt32LittleEndian(span[HookProtocol.PacketHeaderLengthOffset..], header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[HookProtocol.PacketBodyLengthOffset..], body.Length);
        header.CopyTo(span[HookProtocol.PacketPrefixLength..]);
        body.CopyTo(span[(HookProtocol.PacketPrefixLength + header.Length)..]);

        _queue.Enqueue(message);
        _queueChanged.Set();
        return true;
    }

    public bool TryDequeue(out byte[]? message)
    {
        if (!_queue.TryDequeue(out message))
        {
            return false;
        }

        Interlocked.Add(ref _queuedBytes, -message.Length);
        return true;
    }

    public void SendReady(
        uint parserRva,
        int parserLocatorVersion,
        ulong uidRootSlotRva = 0,
        int uidLocatorVersion = 0,
        int equivalentUidPathCount = 0
    )
    {
        Span<byte> message = stackalloc byte[HookProtocol.ReadyMessageLength];
        message.Clear();
        message[0] = HookProtocol.ReadyMessage;
        BinaryPrimitives.WriteUInt64LittleEndian(message[HookProtocol.ReadyParserRvaOffset..], parserRva);
        BinaryPrimitives.WriteInt32LittleEndian(
            message[HookProtocol.ReadyParserLocatorVersionOffset..],
            parserLocatorVersion
        );
        BinaryPrimitives.WriteUInt64LittleEndian(message[HookProtocol.ReadyUidRootSlotRvaOffset..], uidRootSlotRva);
        BinaryPrimitives.WriteInt32LittleEndian(
            message[HookProtocol.ReadyUidLocatorVersionOffset..],
            uidLocatorVersion
        );
        BinaryPrimitives.WriteInt32LittleEndian(
            message[HookProtocol.ReadyEquivalentUidPathCountOffset..],
            equivalentUidPathCount
        );
        Send(message);
    }

    public void SendUid(uint uid)
    {
        Span<byte> message = stackalloc byte[HookProtocol.UidMessageLength];
        message[0] = HookProtocol.UidMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(message[HookProtocol.UidValueOffset..], uid);
        Send(message);
    }

    public void TrySendError(string error)
    {
        try
        {
            if (_pipe is null)
            {
                return;
            }

            var encoded = Encoding.UTF8.GetBytes(error);
            var textLength = Math.Min(encoded.Length, 16 * 1024);
            var message = new byte[HookProtocol.ErrorPrefixLength + textLength];
            message[0] = HookProtocol.ErrorMessage;
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(HookProtocol.ErrorTextLengthOffset), textLength);
            encoded.AsSpan(0, textLength).CopyTo(message.AsSpan(HookProtocol.ErrorPrefixLength));
            Send(message);
        }
        catch
        {
            // 宿主进程可能已经关闭了管道
        }
    }

    public void Send(ReadOnlySpan<byte> message)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Named pipe is not connected.");

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        pipe.Write(length);
        pipe.Write(message);
        pipe.Flush();
    }

    public void WaitForActivity(int millisecondsTimeout)
    {
        _queueChanged.WaitOne(millisecondsTimeout);
    }

    public void RequestShutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        _queueChanged.Set();
    }

    public void Disconnect()
    {
        Volatile.Write(ref _connected, 0);
        Interlocked.Exchange(ref _pipe, null)?.Dispose();
    }

    public static bool TrySetPacketUid(Span<byte> message, uint uid)
    {
        if (message.Length < HookProtocol.UidMessageLength || message[0] != HookProtocol.PacketMessage)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(message[HookProtocol.PacketUidOffset..], uid);
        return true;
    }
}

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
