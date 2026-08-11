namespace Zeitlind.Protocol.Capture;

public sealed record CapturedPacket
{
    public required uint CommandId { get; init; }

    public required ReadOnlyMemory<byte> Header { get; init; }

    public required ReadOnlyMemory<byte> Body { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}
