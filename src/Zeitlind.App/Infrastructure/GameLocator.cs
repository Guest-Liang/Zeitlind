using Microsoft.Win32;
using System.Security;
using Zeitlind.App.Games;

namespace Zeitlind.App.Infrastructure;

internal static class GameLocator
{
    private const string InstallPathValue = "GameInstallPath";

    public static RegistryGameStatus ReadRegistryStatus(IGameModule module)
    {
        var descriptor = module.Descriptor;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(descriptor.RegistryPath);
            var value =
                key?.GetValue(InstallPathValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return new RegistryGameStatus(module, null, "未检测到注册表路径");
            }

            var resolved = ResolveForModule(module, value);
            return new RegistryGameStatus(module, resolved, null);
        }
        catch (Exception exception) when (
            exception
                is ArgumentException
                    or IOException
                    or InvalidDataException
                    or SecurityException
                    or UnauthorizedAccessException
        )
        {
            ApplicationLog.WriteDebug($"{descriptor.DisplayName} 注册表路径无效：{exception}", writeToConsole: false);
            return new RegistryGameStatus(module, null, exception.Message);
        }
    }

    public static ResolvedGame Resolve(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        var normalizedPath = configuredPath.Trim();
        if (
            normalizedPath.Length >= 2
            && (
                (normalizedPath[0] == '"' && normalizedPath[^1] == '"')
                || (normalizedPath[0] == '\'' && normalizedPath[^1] == '\'')
            )
        )
        {
            normalizedPath = normalizedPath[1..^1];
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(normalizedPath);
        var fullPath = Path.GetFullPath(expandedPath);
        if (File.Exists(fullPath))
        {
            var module = GameRegistry.ByExecutableName(Path.GetFileName(fullPath));
            if (module is null)
            {
                throw new InvalidDataException(
                    $"--game 指向的文件必须是 {string.Join(" 或 ", GameRegistry.All.Select(static game => game.Descriptor.ExecutableName))}"
                );
            }

            return new ResolvedGame(module, fullPath);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("--game 指定的游戏目录或 EXE 不存在", fullPath);
        }

        var candidates = GameRegistry
            .All.Select(module => new ResolvedGame(module, Path.Combine(fullPath, module.Descriptor.ExecutableName)))
            .Where(static candidate => File.Exists(candidate.ExecutablePath))
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException(
                $"目录中没有 {string.Join(" 或 ", GameRegistry.All.Select(static game => game.Descriptor.ExecutableName))}",
                fullPath
            ),
            _ => throw new InvalidDataException("目录中同时存在多个受支持的游戏 EXE，请直接指定要使用的 EXE"),
        };
    }

    private static string ResolveForModule(IGameModule module, string configuredPath)
    {
        var resolved = Resolve(configuredPath);
        if (resolved.Module.Descriptor.Kind != module.Descriptor.Kind)
        {
            throw new InvalidDataException(
                $"注册表路径指向 {resolved.Module.Descriptor.DisplayName}，预期为 {module.Descriptor.DisplayName}"
            );
        }

        return resolved.ExecutablePath;
    }
}

internal sealed record ResolvedGame(IGameModule Module, string ExecutablePath);

internal sealed record RegistryGameStatus(IGameModule Module, string? ExecutablePath, string? Error)
{
    public string MenuText =>
        ExecutablePath is not null
            ? $"{Module.Descriptor.DisplayName}（注册表：已检测 {ExecutablePath}）"
            : $"{Module.Descriptor.DisplayName}（注册表：{Error ?? "未检测"}）";
}
