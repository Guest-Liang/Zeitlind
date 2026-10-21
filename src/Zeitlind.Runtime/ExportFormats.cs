using Zeitlind.Core.Games;
using Zeitlind.Runtime.Games;

namespace Zeitlind.Runtime;

internal static class ExportFormats
{
    public static ExportFormatOption[] For(IGameModule module)
    {
        return module.Descriptor.Kind == GameKind.GI
            ?
            [
                new(ExportTarget.AchievementBackup, "成就数据备份 · 保留全部原始字段"),
                new(ExportTarget.Uiaf, "UIAF v1.1"),
                new(ExportTarget.UiafV12, "UIAF v1.2 · 实验性 / 非官方", IsExperimental: true),
            ]
            :
            [
                new(ExportTarget.AchievementBackup, "成就数据备份 · 保留全部原始字段"),
                new(ExportTarget.Liyin, "Liyin JSON · 仅已完成成就"),
                new(ExportTarget.Uiaf, "UIAF v1.2 · 实验性 / 非官方", IsExperimental: true),
            ];
    }

    public static ExportFormatOption Validate(IGameModule module, ExportTarget target)
    {
        return For(module).FirstOrDefault(option => option.Target == target)
            ?? throw new InvalidDataException($"{module.Descriptor.DisplayName}不支持所选导出格式");
    }
}

internal sealed record ExportFormatOption(ExportTarget Target, string Label, bool IsExperimental = false);
