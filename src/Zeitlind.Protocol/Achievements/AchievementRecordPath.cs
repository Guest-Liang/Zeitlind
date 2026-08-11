using System.Globalization;

namespace Zeitlind.Protocol.Achievements;

internal static class AchievementRecordPath
{
    public static uint[] Parse(string path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("$.", StringComparison.Ordinal)
            || !path.EndsWith("[]", StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("成就记录路径格式错误", nameof(path));
        }

        var segments = path[2..^2].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new uint[segments.Length];
        if (result.Length == 0)
        {
            throw new ArgumentException("成就记录路径不能为空", nameof(path));
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (
                !uint.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out result[index])
                || result[index] == 0
            )
            {
                throw new ArgumentException($"成就记录路径包含无效字段：{segments[index]}", nameof(path));
            }
        }

        return result;
    }

    public static string Format(IReadOnlyList<uint> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0 || path.Any(static fieldNumber => fieldNumber == 0))
        {
            throw new ArgumentException("成就记录路径字段号必须非零", nameof(path));
        }

        return "$."
            + string.Join('.', path.Select(static fieldNumber => fieldNumber.ToString(CultureInfo.InvariantCulture)))
            + "[]";
    }
}
