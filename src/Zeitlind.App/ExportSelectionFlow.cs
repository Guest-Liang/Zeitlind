using Zeitlind.App.Infrastructure;
using Zeitlind.Runtime;
using Zeitlind.Runtime.Games;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.App;

internal static class ExportSelectionFlow
{
    public static ExportTarget Select(
        IGameModule module,
        ExportTarget? configuredTarget,
        CancellationToken cancellationToken
    )
    {
        if (configuredTarget is { } target)
        {
            ValidateTarget(module, target);
            return target;
        }

        if (Console.IsInputRedirected)
        {
            ApplicationLog.WriteInfo("标准输入不可交互，默认导出 Zeitlind 成就数据备份");
            return ExportTarget.AchievementBackup;
        }

        Console.WriteLine();
        ApplicationLog.WriteInfo($"请选择 {module.Descriptor.DisplayName} 导出格式（↑/↓ 选择，Enter 确认）：");
        var options = ExportFormats.For(module);
        var labels = options.Select(static option => option.Label).ToArray();
        var selected = ConsoleSelectionMenu.Read(labels, 0, cancellationToken);
        var option = options[selected];
        ApplicationLog.WriteInfo($"已选择：{option.Label}");
        target = option.Target;
        WarnIfExperimental(option);
        return target;
    }

    public static void ValidateConfiguredTarget(IGameModule module, ExportTarget target)
    {
        var option = ValidateTarget(module, target);
        ApplicationLog.WriteInfo($"导出格式来源：命令行 --format {ToCliValue(target)}");
        WarnIfExperimental(option);
    }

    public static string ToCliValue(ExportTarget target)
    {
        return target switch
        {
            ExportTarget.AchievementBackup => "backup",
            ExportTarget.Liyin => "liyin",
            ExportTarget.Uiaf => "uiaf",
            ExportTarget.UiafV12 => "uiaf12",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
        };
    }

    private static ExportFormatOption ValidateTarget(IGameModule module, ExportTarget target)
    {
        try
        {
            return ExportFormats.Validate(module, target);
        }
        catch (InvalidDataException)
        {
            var supported = string.Join("、", ExportFormats.For(module).Select(option => ToCliValue(option.Target)));
            throw new InvalidDataException(
                $"{module.Descriptor.DisplayName}不支持 --format {ToCliValue(target)}，请使用 {supported}"
            );
        }
    }

    private static void WarnIfExperimental(ExportFormatOption option)
    {
        if (option.IsExperimental)
        {
            ApplicationLog.WriteWarning("提示：现行正式 UIAF 尚未定义 v1.2；Zeitlind 目前导出为实验性支持");
            ApplicationLog.WriteWarning("可查看 https://github.com/orgs/UIGF-org/discussions/18 以获取更多信息");
        }
    }
}
