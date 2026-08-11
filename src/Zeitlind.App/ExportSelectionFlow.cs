using Zeitlind.App.Games;
using Zeitlind.App.Infrastructure;

namespace Zeitlind.App;

internal static class ExportSelectionFlow
{
    private static readonly ExportOption[] Options =
    [
        new(ExportTarget.AchievementBackup, "Zeitlind 成就数据备份（保留全部原始字段）"),
        new(ExportTarget.Liyin, "Zeitlind Liyin JSON（仅包含已完成的成就）"),
        new(ExportTarget.UiafExperimental, "Zeitlind 实验性 UIAF v1.2"),
    ];

    public static ExportTarget Select(
        IGameModule module,
        ExportTarget? configuredTarget,
        CancellationToken cancellationToken
    )
    {
        if (configuredTarget is { } target)
        {
            ApplicationLog.WriteInfo($"导出格式来源：命令行 --format {ToCliValue(target)}");
            WarnIfExperimental(target);
            return target;
        }

        if (Console.IsInputRedirected)
        {
            ApplicationLog.WriteInfo("标准输入不可交互，默认导出 Zeitlind 成就数据备份");
            return ExportTarget.AchievementBackup;
        }

        Console.WriteLine();
        ApplicationLog.WriteInfo($"请选择 {module.Descriptor.DisplayName} 导出格式（↑/↓ 选择，Enter 确认）：");
        var labels = Options.Select(static option => option.Label).ToArray();
        var selected = ConsoleSelectionMenu.Read(labels, 0, cancellationToken);
        var option = Options[selected];
        ApplicationLog.WriteInfo($"已选择：{option.Label}");
        target = option.Target;
        WarnIfExperimental(target);
        return target;
    }

    public static string ToCliValue(ExportTarget target)
    {
        return target switch
        {
            ExportTarget.AchievementBackup => "backup",
            ExportTarget.Liyin => "liyin",
            ExportTarget.UiafExperimental => "uiaf",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    private static void WarnIfExperimental(ExportTarget target)
    {
        if (target == ExportTarget.UiafExperimental)
        {
            ApplicationLog.WriteWarning("提示：现行正式 UIAF 尚未定义v1.2；Zeitlind 目前导出为实验性支持");
            ApplicationLog.WriteWarning("可查看 https://github.com/orgs/UIGF-org/discussions/18 以获取更多信息");
        }
    }

    private readonly record struct ExportOption(ExportTarget Target, string Label);
}

internal enum ExportTarget
{
    AchievementBackup,
    Liyin,
    UiafExperimental,
}
