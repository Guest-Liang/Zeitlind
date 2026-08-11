using System.Globalization;
using Zeitlind.Protocol.Capture;

namespace Zeitlind.App.Games;

internal sealed class HsrPacketCaptureDiagnostics
{
    private readonly Dictionary<uint, CommandStatistics> _commands = [];

    public void Observe(CapturedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!_commands.TryGetValue(packet.CommandId, out var statistics))
        {
            statistics = new CommandStatistics();
            _commands.Add(packet.CommandId, statistics);
        }

        statistics.Count++;
        statistics.TotalBodyBytes += packet.Body.Length;
        statistics.MaximumBodyBytes = Math.Max(statistics.MaximumBodyBytes, packet.Body.Length);
    }

    public string FormatForLog(int limit = 8)
    {
        if (_commands.Count == 0)
        {
            return "命令统计：尚未收到数据包";
        }

        var frequent = _commands
            .OrderByDescending(static pair => pair.Value.Count)
            .ThenByDescending(static pair => pair.Value.MaximumBodyBytes)
            .ThenBy(static pair => pair.Key)
            .Take(limit)
            .Select(FormatEntry);
        var largest = _commands
            .OrderByDescending(static pair => pair.Value.MaximumBodyBytes)
            .ThenByDescending(static pair => pair.Value.TotalBodyBytes)
            .ThenBy(static pair => pair.Key)
            .Take(limit)
            .Select(FormatEntry);

        return $"命令统计（命令=包数/最大正文）：高频 [{string.Join(", ", frequent)}]；"
            + $"大包 [{string.Join(", ", largest)}]；共 {_commands.Count} 种命令";
    }

    private static string FormatEntry(KeyValuePair<uint, CommandStatistics> pair)
    {
        return $"{pair.Key.ToString(CultureInfo.InvariantCulture)}="
            + $"{pair.Value.Count.ToString(CultureInfo.InvariantCulture)}/"
            + FormatByteCount(pair.Value.MaximumBodyBytes);
    }

    private static string FormatByteCount(int bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)}B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{(bytes / 1024D).ToString("0.0", CultureInfo.InvariantCulture)}KiB";
        }

        return $"{(bytes / (1024D * 1024D)).ToString("0.00", CultureInfo.InvariantCulture)}MiB";
    }

    private sealed class CommandStatistics
    {
        public int Count { get; set; }

        public long TotalBodyBytes { get; set; }

        public int MaximumBodyBytes { get; set; }
    }
}
