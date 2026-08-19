using Zeitlind.App.Games;
using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Games;

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
            EnsureSupported(module, target);
            return target;
        }

        if (Console.IsInputRedirected)
        {
            ApplicationLog.WriteInfo("标准输入不可交互，默认导出 Zeitlind 成就数据备份");
            return ExportTarget.AchievementBackup;
        }

        Console.WriteLine();
        ApplicationLog.WriteInfo($"请选择 {module.Descriptor.DisplayName} 导出格式（↑/↓ 选择，Enter 确认）：");
        var options = OptionsFor(module);
        var labels = options.Select(static option => option.Label).ToArray();
        var selected = ConsoleSelectionMenu.Read(labels, 0, cancellationToken);
        var option = options[selected];
        ApplicationLog.WriteInfo($"已选择：{option.Label}");
        target = option.Target;
        WarnIfExperimental(module, target);
        return target;
    }

    public static void ValidateConfiguredTarget(IGameModule module, ExportTarget target)
    {
        EnsureSupported(module, target);
        ApplicationLog.WriteInfo($"导出格式来源：命令行 --format {ToCliValue(target)}");
        WarnIfExperimental(module, target);
    }

    public static string ToCliValue(ExportTarget target)
    {
        return target switch
        {
            ExportTarget.AchievementBackup => "backup",
            ExportTarget.Liyin => "liyin",
            ExportTarget.UiafExperimental => "uiaf",
            ExportTarget.UiafV12 => "uiaf12",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    private static ExportOption[] OptionsFor(IGameModule module)
    {
        return module.Descriptor.Kind == GameKind.GI
            ?
            [
                new(ExportTarget.AchievementBackup, "Zeitlind 成就数据备份（保留全部原始字段）"),
                new(ExportTarget.UiafExperimental, "UIAF v1.1"),
                new(ExportTarget.UiafV12, "Zeitlind 实验性 UIAF v1.2"),
            ]
            : Options;
    }

    private static void EnsureSupported(IGameModule module, ExportTarget target)
    {
        if (module.Descriptor.Kind == GameKind.GI && target == ExportTarget.Liyin)
        {
            throw new InvalidDataException("原神国服不支持 --format liyin，请使用 backup、uiaf 或 uiaf12");
        }

        if (module.Descriptor.Kind != GameKind.GI && target == ExportTarget.UiafV12)
        {
            throw new InvalidDataException(
                $"{module.Descriptor.DisplayName} 请使用 --format uiaf 导出实验性 UIAF v1.2"
            );
        }
    }

    private static void WarnIfExperimental(IGameModule module, ExportTarget target)
    {
        if (
            target == ExportTarget.UiafV12
            || (target == ExportTarget.UiafExperimental && module.Descriptor.Kind != GameKind.GI)
        )
        {
            ApplicationLog.WriteWarning("提示：现行正式 UIAF 尚未定义 v1.2；Zeitlind 目前导出为实验性支持");
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
    UiafV12,
}
