namespace Zeitlind.Runtime;

internal enum ExportStage
{
    Starting,
    WaitingForLogin,
    ReadingAchievements,
    ConfirmingAccount,
    ClosingGame,
}

internal sealed record ExportProgress(ExportStage Stage, string Message);
