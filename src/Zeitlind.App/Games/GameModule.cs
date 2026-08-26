using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Core.Games;
using Zeitlind.Protocol.Capture;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App.Games;

internal sealed record GameDescriptor(
    GameKind Kind,
    string Id,
    string DisplayName,
    string ExecutableName,
    string ProcessName,
    string RegistryPath,
    string HookResourceName,
    string HookEntryPoint
);

internal readonly record struct PlayerIdentityEvidence(ulong Uid, string Detail);

internal interface IGameModule
{
    GameDescriptor Descriptor { get; }

    string ValidateInstallation(string executablePath);

    IGameCaptureAdapter CreateCaptureAdapter(AchievementCatalog catalog, string gameVersion);

    string Serialize(ExportTarget target, AchievementSnapshot snapshot, ulong? uid, AchievementCatalog catalog);

    string BuildExportSummary(ExportTarget target, AchievementSnapshot snapshot, AchievementCatalog catalog);
}

internal interface IGameCaptureAdapter
{
    string StartInstruction { get; }

    bool CanExportWithoutConfirmedUid => false;

    void OnHookReady(HookReadyMessage message);

    void ObservePacket(CapturedPacket packet) { }

    bool TryReadIdentity(out PlayerIdentityEvidence evidence)
    {
        evidence = default;
        return false;
    }

    string FormatIdentityDiagnostics() => "当前游戏未配置本地 UID 来源";

    bool TryDecodeSnapshot(CapturedPacket packet, out AchievementSnapshot? snapshot);

    string FormatDiagnostics();

    string FormatSnapshotDetails(AchievementSnapshot snapshot);
}

internal static class GameRegistry
{
    public static IReadOnlyList<IGameModule> All { get; } =
    [ZzzCnGameModule.Instance, HsrCnGameModule.Instance, GiCnGameModule.Instance];

    public static IGameModule? ByExecutableName(string fileName)
    {
        return All.SingleOrDefault(module =>
            fileName.Equals(module.Descriptor.ExecutableName, StringComparison.OrdinalIgnoreCase)
        );
    }
}
