using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Core.Profiles;
using Zeitlind.Formats.Backup;
using Zeitlind.Formats.Genshin;
using Zeitlind.Protocol.Achievements;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Identity;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App.Games;

internal sealed class GiCnGameModule : IGameModule
{
    private const string ExpectedPublisher = "miHoYo";
    private const string ExpectedProduct = "原神";

    public static GiCnGameModule Instance { get; } = new();

    public GameDescriptor Descriptor { get; } =
        new(
            GameKind.GI,
            "gi-cn",
            "原神国服",
            "YuanShen.exe",
            "YuanShen",
            @"Software\miHoYo\HYP\1_1\hk4e_cn",
            "Zeitlind.Hooks.gi.dll",
            "ZeitlindGiHookMain"
        );

    public string ValidateInstallation(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidDataException("无法确定游戏安装目录");
        var dataDirectory = Path.Combine(directory, "YuanShen_Data");
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException("原神目录缺少 YuanShen_Data");
        }

        ValidateAppInfo(dataDirectory);
        return ValidateConfig(directory);
    }

    public IGameCaptureAdapter CreateCaptureAdapter(AchievementCatalog catalog, string gameVersion)
    {
        return new GiCaptureAdapter(catalog, gameVersion);
    }

    public string Serialize(ExportTarget target, AchievementSnapshot snapshot, ulong? uid, AchievementCatalog catalog)
    {
        return target switch
        {
            ExportTarget.AchievementBackup => AchievementBackupExporter.Serialize(
                snapshot,
                uid,
                catalog.LatestVersion,
                catalog.Count
            ),
            ExportTarget.UiafExperimental => GenshinUiafExporter.Serialize(snapshot, ApplicationBuildInfo.Version),
            ExportTarget.UiafV12 => GenshinUiafV12Exporter.Serialize(snapshot, uid),
            ExportTarget.Liyin => throw new InvalidDataException(
                "原神国服不支持 Liyin 导出，请使用 backup、uiaf 或 uiaf12"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    public string BuildExportSummary(ExportTarget target, AchievementSnapshot snapshot, AchievementCatalog catalog)
    {
        var completed = snapshot.Records.Count(static record => record.IsCompleted);
        var uiaf = snapshot.Records.Count(record =>
            (record.Status is >= 2 and <= 3) || record.Progress is > 0 || record.IsCompleted
        );
        return target switch
        {
            ExportTarget.AchievementBackup =>
                $"导出完成：保留服务端返回的 {snapshot.Records.Count} 条原神成就记录，其中 {completed} 条已完成",
            ExportTarget.UiafExperimental => $"导出完成：写入 UIAF v1.1 的 {uiaf} 条原神成就记录",
            ExportTarget.UiafV12 => $"导出完成：写入实验性 UIAF v1.2 的 {uiaf} 条原神成就记录",
            ExportTarget.Liyin => throw new InvalidDataException("原神国服不支持 Liyin 导出"),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    private static void ValidateAppInfo(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "app.info");
        if (!File.Exists(path))
        {
            return;
        }

        using var reader = new StringReader(BoundedTextFile.ReadAllText(path, "app.info"));
        var publisher = reader.ReadLine();
        var product = reader.ReadLine();
        if (
            publisher is null
            || product is null
            || !publisher.Trim().Equals(ExpectedPublisher, StringComparison.Ordinal)
            || !product.Trim().Equals(ExpectedProduct, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("app.info 与国服《原神》不匹配");
        }
    }

    private static string ValidateConfig(string directory)
    {
        var path = Path.Combine(directory, "config.ini");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("原神目录缺少 config.ini", path);
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
        using var reader = new StringReader(BoundedTextFile.ReadAllText(path, "config.ini"));
        while (reader.ReadLine() is { } rawLine)
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

    private sealed class GiCaptureAdapter : IGameCaptureAdapter
    {
        private static readonly GenshinAchievementProtocolProfile Profile = new()
        {
            FullSnapshotCommandId = 29910,
            RecordFieldPath = "$.5[]",
            IdFieldNumber = 5,
            StatusFieldNumber = 8,
            FinishTimestampFieldNumber = 2,
            ProgressFieldNumber = 6,
            PackedVarintFieldNumbers = [],
        };

        private readonly GenshinAchievementSnapshotDecoder _decoder;
        private readonly PacketCaptureDiagnostics _diagnostics = new();

        public GiCaptureAdapter(AchievementCatalog catalog, string gameVersion)
        {
            _decoder = new GenshinAchievementSnapshotDecoder(catalog, gameVersion, Profile);
        }

        public string StartInstruction => "请正常登录并打开成就页面；Zeitlind 会等待原神完整成就快照和当前 UID";

        public bool CanExportWithoutConfirmedUid => true;

        public void OnHookReady(HookReadyMessage message)
        {
            ApplicationLog.WriteDebug(
                $"原神 Hook：解析器 RVA 0x{message.ParserRva:X}，定位版本 {message.ParserLocatorVersion}",
                writeToConsole: true
            );
        }

        public void ObservePacket(CapturedPacket packet)
        {
            _diagnostics.Observe(packet);
        }

        public bool TryDecodeIdentity(CapturedPacket packet, out PlayerIdentityEvidence evidence)
        {
            if (GenshinPlayerIdentityDecoder.TryDecode(packet, out evidence))
            {
                return true;
            }

            evidence = default;
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
            return $"{_diagnostics.FormatForLog(limit: 12)}；正式协议提示：命令 {Profile.FullSnapshotCommandId}；{candidate}";
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
