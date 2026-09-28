using Zeitlind.Core.Achievements;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App.Games;

internal interface IGameModule
{
    GameDescriptor Descriptor { get; }

    string ValidateInstallation(string executablePath);

    IGameCaptureAdapter CreateCaptureAdapter(AchievementCatalog catalog, string gameVersion);

    string Serialize(ExportTarget target, AchievementSnapshot snapshot, ulong? uid, AchievementCatalog catalog);

    string BuildExportSummary(ExportTarget target, AchievementSnapshot snapshot, AchievementCatalog catalog);
}
