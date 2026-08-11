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

internal interface IGameModule
{
    GameDescriptor Descriptor { get; }

    string ValidateInstallation(string executablePath);

    IGameCaptureAdapter CreateCaptureAdapter(AchievementCatalog catalog, string gameVersion);

    string Serialize(ExportTarget target, AchievementSnapshot snapshot, uint uid, AchievementCatalog catalog);

    string BuildExportSummary(ExportTarget target, AchievementSnapshot snapshot, AchievementCatalog catalog);
}

internal interface IGameCaptureAdapter
{
    string StartInstruction { get; }

    void OnHookReady(HookReadyMessage message);

    void ObservePacket(CapturedPacket packet) { }

    bool TryDecodeIdentity(CapturedPacket packet, out uint uid, out string detail)
    {
        uid = 0;
        detail = string.Empty;
        return false;
    }

    bool TryDecodeSnapshot(CapturedPacket packet, out AchievementSnapshot? snapshot);

    string FormatDiagnostics();

    string FormatSnapshotDetails(AchievementSnapshot snapshot);
}

internal static class GameRegistry
{
    public static IReadOnlyList<IGameModule> All { get; } = [ZzzCnGameModule.Instance, HsrCnGameModule.Instance];

    public static IGameModule ByKind(GameKind kind)
    {
        return All.Single(module => module.Descriptor.Kind == kind);
    }

    public static IGameModule? ByExecutableName(string fileName)
    {
        return All.SingleOrDefault(module =>
            fileName.Equals(module.Descriptor.ExecutableName, StringComparison.OrdinalIgnoreCase)
        );
    }
}
