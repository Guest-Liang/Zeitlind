namespace Zeitlind.App.Infrastructure;

internal static class ConfiguredPath
{
    public static string Resolve(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim();
        if (
            normalized.Length >= 2
            && (
                (normalized[0] == '"' && normalized[^1] == '"')
                || (normalized[0] == '\'' && normalized[^1] == '\'')
            )
        )
        {
            normalized = normalized[1..^1];
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(normalized);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(normalized));
    }
}
