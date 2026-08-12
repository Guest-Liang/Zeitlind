using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Core.Profiles;
using Zeitlind.Formats.Backup;
using Zeitlind.Formats.Zzz;
using Zeitlind.Protocol.Achievements;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App.Games;

internal sealed class ZzzCnGameModule : IGameModule
{
    private const string ProductionMarker = "CNPRODWin";
    private const string SupportedVersionPrefix = "CNPRODWin3.1.";

    public static ZzzCnGameModule Instance { get; } = new();

    public GameDescriptor Descriptor { get; } =
        new(
            GameKind.ZZZ,
            "zzz-cn",
            "绝区零国服",
            "ZenlessZoneZero.exe",
            "ZenlessZoneZero",
            @"Software\miHoYo\HYP\1_1\nap_cn",
            "Zeitlind.Hooks.zzz.dll",
            "ZeitlindZzzHookMain"
        );

    public string ValidateInstallation(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidDataException("无法确定游戏安装目录");
        var versionPath = Path.Combine(directory, "version_info");
        if (!File.Exists(versionPath))
        {
            throw new FileNotFoundException("游戏目录缺少 version_info，无法确认绝区零渠道", versionPath);
        }

        var marker = BoundedTextFile.ReadAllText(versionPath, "version_info").Trim();
        if (!marker.StartsWith(ProductionMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"构建标记 {marker} 不是 Zeitlind 支持的绝区零国服正式渠道");
        }

        if (!marker.StartsWith(SupportedVersionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"构建标记为 {marker}；当前 Zeitlind 仅支持绝区零国服 3.1");
        }

        EnsureGameAssembly(directory);
        return marker;
    }

    public IGameCaptureAdapter CreateCaptureAdapter(AchievementCatalog catalog, string gameVersion)
    {
        return new ZzzCaptureAdapter(catalog, gameVersion);
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
            ExportTarget.Liyin => ZzzLiyinExporter.Serialize(snapshot, uid),
            ExportTarget.UiafExperimental => ZzzUiafExporter.Serialize(snapshot, uid),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    public string BuildExportSummary(ExportTarget target, AchievementSnapshot snapshot, AchievementCatalog catalog)
    {
        var completed = snapshot.Records.Count(static record => record.IsCompleted);
        return target switch
        {
            ExportTarget.AchievementBackup =>
                $"导出完成：保留服务端返回的 {snapshot.Records.Count} 条绝区零成就记录，其中 {completed} 条已完成",
            ExportTarget.Liyin => $"导出完成：写入 {completed} 条具有完成证据的绝区零成就 ID",
            ExportTarget.UiafExperimental =>
                $"导出完成：写入服务端实际返回的 {snapshot.Records.Count} 条绝区零成就记录",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    private static void EnsureGameAssembly(string directory)
    {
        var path = Path.Combine(directory, "GameAssembly.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("绝区零目录缺少 GameAssembly.dll", path);
        }
    }

    private sealed class ZzzCaptureAdapter : IGameCaptureAdapter
    {
        private static readonly ZzzAchievementProtocolProfile Profile = new()
        {
            FullSnapshotCommandId = 6515,
            RecordFieldPath = "$.9.1691.1[]",
            IdFieldNumber = 1,
            FinishTimestampFieldNumber = 3,
            CompletedFlagFieldNumber = 4,
        };

        private readonly ZzzAchievementSnapshotDecoder _decoder;

        public ZzzCaptureAdapter(AchievementCatalog catalog, string gameVersion)
        {
            _decoder = new ZzzAchievementSnapshotDecoder(catalog, gameVersion, Profile);
        }

        public string StartInstruction => "请正常登录；Zeitlind 会等待绝区零完整成就快照和当前 UID";

        public void OnHookReady(HookReadyMessage message)
        {
            if (message.UidRootSlotRva == 0 || message.UidLocatorVersion == 0)
            {
                throw new InvalidDataException("绝区零 Hook 未报告有效的 UID 定位信息");
            }

            ApplicationLog.WriteDebug(
                $"绝区零 Hook：解析器 RVA 0x{message.ParserRva:X}，定位版本 {message.ParserLocatorVersion}；"
                    + $"UID RootSlot RVA 0x{message.UidRootSlotRva:X}，定位版本 {message.UidLocatorVersion}，"
                    + $"{message.EquivalentUidPathCount} 条等价路径",
                writeToConsole: true
            );
        }

        public bool TryDecodeSnapshot(CapturedPacket packet, out AchievementSnapshot? snapshot)
        {
            return _decoder.TryDecode(packet, out snapshot);
        }

        public string FormatDiagnostics()
        {
            return $"绝区零正式协议：命令 {Profile.FullSnapshotCommandId}，路径 {Profile.RecordFieldPath}";
        }

        public string FormatSnapshotDetails(AchievementSnapshot snapshot)
        {
            return $"命令 {snapshot.SourceCommandId}，路径 {snapshot.RecordFieldPath}，"
                + $"ID/完成时间/完成标志字段 {snapshot.IdFieldNumber}/{snapshot.FinishTimestampFieldNumber}/{snapshot.CompletedFlagFieldNumber}";
        }
    }
}
