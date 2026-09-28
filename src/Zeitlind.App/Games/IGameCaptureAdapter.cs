using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Protocol.Capture;

namespace Zeitlind.App.Games;

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
