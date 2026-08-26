namespace Zeitlind.Core.Interop;

public static class HookProtocol
{
    public const byte ReadyMessage = 1;
    public const byte PacketMessage = 2;
    public const byte ErrorMessage = 3;

    public const int ReadyMessageLength = 13;
    public const int ReadyParserRvaOffset = 1;
    public const int ReadyParserLocatorVersionOffset = 9;

    public const int PacketPrefixLength = 11;
    public const int PacketCommandIdOffset = 1;
    public const int PacketHeaderLengthOffset = 3;
    public const int PacketBodyLengthOffset = 7;
    public const int MaximumPacketHeaderLength = ushort.MaxValue;
    public const int MaximumPacketBodyLength = 32 * 1024 * 1024;
    public const int MaximumMessageLength = PacketPrefixLength + MaximumPacketHeaderLength + MaximumPacketBodyLength;

    public const int ErrorPrefixLength = 5;
    public const int ErrorTextLengthOffset = 1;
    public const int MaximumErrorTextLength = 16 * 1024;
    public const int MaximumErrorMessageLength = ErrorPrefixLength + MaximumErrorTextLength;

    public static string GetPipeName(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return $"Zeitlind-{processId}";
    }
}
