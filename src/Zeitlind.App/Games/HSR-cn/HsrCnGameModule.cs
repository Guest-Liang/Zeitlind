using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Core.Profiles;
using Zeitlind.Formats.Backup;
using Zeitlind.Formats.Hsr;
using Zeitlind.Protocol.Achievements;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Identity;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App.Games;

internal sealed class HsrCnGameModule : IGameModule
{
    private const string ExpectedPublisher = "miHoYo";
    private const string ExpectedProduct = "崩坏：星穹铁道";

    public static HsrCnGameModule Instance { get; } = new();

    public GameDescriptor Descriptor { get; } =
        new(
            GameKind.HSR,
            "hsr-cn",
            "崩坏：星穹铁道国服",
            "StarRail.exe",
            "StarRail",
            @"Software\miHoYo\HYP\1_1\hkrpg_cn",
            "Zeitlind.Hooks.hsr.dll",
            "ZeitlindHsrHookMain"
        );

    public string ValidateInstallation(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidDataException("无法确定游戏安装目录");
        var gameAssemblyPath = Path.Combine(directory, "GameAssembly.dll");
        if (!File.Exists(gameAssemblyPath))
        {
            throw new FileNotFoundException("星穹铁道目录缺少 GameAssembly.dll", gameAssemblyPath);
        }

        ValidateAppInfo(directory);
        return ValidateConfig(directory);
    }

    public IGameCaptureAdapter CreateCaptureAdapter(AchievementCatalog catalog, string gameVersion)
    {
        return new HsrCaptureAdapter(catalog, gameVersion);
    }

    public string Serialize(ExportTarget target, AchievementSnapshot snapshot, uint uid, AchievementCatalog catalog)
    {
        return target switch
        {
            ExportTarget.AchievementBackup => AchievementBackupExporter.Serialize(
                snapshot,
                uid,
                catalog.LatestVersion,
                catalog.Count
            ),
            ExportTarget.Liyin => HsrLiyinExporter.Serialize(snapshot, uid, catalog.Ids),
            ExportTarget.UiafExperimental => HsrUiafExporter.Serialize(snapshot, uid, catalog.Ids),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    public string BuildExportSummary(ExportTarget target, AchievementSnapshot snapshot, AchievementCatalog catalog)
    {
        var completed = snapshot.Records.Count(static record => record.IsCompleted);
        var knownCompleted = snapshot.Records.Count(record => catalog.Ids.Contains(record.Id) && record.IsCompleted);
        var uiaf = snapshot.Records.Count(record => catalog.Ids.Contains(record.Id) && record.Status is >= 1 and <= 3);
        return target switch
        {
            ExportTarget.AchievementBackup =>
                $"导出完成：保留服务端返回的 {snapshot.Records.Count} 条星穹铁道成就记录，其中 {completed} 条已完成",
            ExportTarget.Liyin => $"导出完成：写入元数据内 {knownCompleted} 条已完成的星穹铁道成就 ID",
            ExportTarget.UiafExperimental => $"导出完成：写入元数据内且状态为 1/2/3 的 {uiaf} 条星穹铁道成就记录",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    private static void ValidateAppInfo(string directory)
    {
        var path = Path.Combine(directory, "StarRail_Data", "app.info");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("星穹铁道目录缺少 StarRail_Data\\app.info", path);
        }

        var lines = File.ReadAllLines(path);
        if (
            lines.Length < 2
            || !lines[0].Trim().Equals(ExpectedPublisher, StringComparison.Ordinal)
            || !lines[1].Trim().Equals(ExpectedProduct, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("app.info 与国服《崩坏：星穹铁道》不匹配");
        }
    }

    private static string ValidateConfig(string directory)
    {
        var path = Path.Combine(directory, "config.ini");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("星穹铁道目录缺少 config.ini", path);
        }

        var general = ReadIniSection(path, "General");
        if (
            !general.TryGetValue("channel", out var channel)
            || channel != "1"
            || !general.TryGetValue("sub_channel", out var subChannel)
            || subChannel != "1"
        )
        {
            throw new InvalidDataException("config.ini 的 channel/sub_channel 不是国服正式渠道");
        }

        if (!general.TryGetValue("game_version", out var version) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException("config.ini 缺少有效的 game_version");
        }

        return version.Trim();
    }

    private static Dictionary<string, string> ReadIniSection(string path, string expectedSection)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = line[1..^1].Trim().Equals(expectedSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return values;
    }

    private sealed class HsrCaptureAdapter : IGameCaptureAdapter
    {
        private static readonly HsrAchievementProtocolProfile Profile = new()
        {
            FullSnapshotCommandId = 978,
            RecordFieldPath = "$.13[]",
            IdFieldNumber = 14,
            StatusFieldNumber = 15,
            FinishTimestampFieldNumber = 2,
            ProgressFieldNumber = 1,
            PackedVarintFieldNumbers = [3],
        };

        private readonly HsrAchievementSnapshotDecoder _decoder;
        private readonly HsrPacketCaptureDiagnostics _diagnostics = new();

        public HsrCaptureAdapter(AchievementCatalog catalog, string gameVersion)
        {
            _decoder = new HsrAchievementSnapshotDecoder(catalog, gameVersion, Profile);
        }

        public string StartInstruction => "请正常登录并打开成就页面；Zeitlind 会等待星穹铁道完整成就快照和登录 UID";

        public void OnHookReady(HookReadyMessage message)
        {
            ApplicationLog.WriteDebug(
                $"星穹铁道 Hook：解析器 RVA 0x{message.ParserRva:X}，定位版本 {message.ParserLocatorVersion}",
                writeToConsole: true
            );
        }

        public void ObservePacket(CapturedPacket packet)
        {
            _diagnostics.Observe(packet);
        }

        public bool TryDecodeIdentity(CapturedPacket packet, out uint uid, out string detail)
        {
            if (PlayerIdentityDecoder.TryDecode(packet, out uid, out var fieldNumber))
            {
                detail = $"命令 {packet.CommandId}，字段 {fieldNumber}";
                return true;
            }

            detail = string.Empty;
            return false;
        }

        public bool TryDecodeSnapshot(CapturedPacket packet, out AchievementSnapshot? snapshot)
        {
            return _decoder.TryDecode(packet, out snapshot);
        }

        public string FormatDiagnostics()
        {
            var diagnostic = _decoder.BestCandidate;
            var candidate = diagnostic is null
                ? "成就候选：尚未发现至少 3 条且元数据命中率达到 60% 的记录组"
                : $"最佳成就候选：{FormatCandidateDiagnostic(diagnostic)}";
            return $"{_diagnostics.FormatForLog(limit: 12)}；{candidate}";
        }

        public string FormatSnapshotDetails(AchievementSnapshot snapshot)
        {
            return $"命令 {snapshot.SourceCommandId}，路径 {snapshot.RecordFieldPath}，"
                + $"ID/状态/完成时间/进度字段 {snapshot.IdFieldNumber}/{Display(snapshot.StatusFieldNumber)}/"
                + $"{Display(snapshot.FinishTimestampFieldNumber)}/{Display(snapshot.ProgressFieldNumber)}";
        }

        private static string Display(uint? value)
        {
            return value?.ToString() ?? "未识别";
        }

        private static string FormatCandidateDiagnostic(AchievementCandidateDiagnostic diagnostic)
        {
            return $"命令 {diagnostic.CommandId}，路径 {diagnostic.RecordFieldPath}，"
                + $"ID 字段 {diagnostic.IdFieldNumber}，状态字段 {Display(diagnostic.StatusFieldNumber)}，"
                + $"完成时间字段 {Display(diagnostic.FinishTimestampFieldNumber)}，"
                + $"进度字段 {Display(diagnostic.ProgressFieldNumber)}；记录 {diagnostic.RecordCount} 条，"
                + $"元数据命中 {diagnostic.CatalogMatchCount} 条，未知 ID {diagnostic.UnknownIdCount} 条，"
                + $"完成时间证据 {diagnostic.CompletionEvidenceCount} 条；{diagnostic.Decision}";
        }
    }
}
