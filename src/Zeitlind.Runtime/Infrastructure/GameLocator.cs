using System.Security;
using Microsoft.Win32;
using Zeitlind.Runtime.Games;

namespace Zeitlind.Runtime.Infrastructure;

internal static class GameLocator
{
    private const string InstallPathValue = "GameInstallPath";

    public static RegistryGameStatus ReadRegistryStatus(IGameModule module)
    {
        var descriptor = module.Descriptor;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(descriptor.RegistryPath);
            var value = key?.GetValue(InstallPathValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return ResolveRegistryValue(module, value);
        }
        catch (Exception exception)
            when (exception
                    is ArgumentException
                        or IOException
                        or InvalidDataException
                        or SecurityException
                        or UnauthorizedAccessException
            )
        {
            ApplicationLog.WriteWarningException($"{descriptor.DisplayName} 注册表读取失败", exception);
            return new RegistryGameStatus(module, RegistryGameState.ReadFailed, null, exception.Message);
        }
    }

    private static RegistryGameStatus ResolveRegistryValue(IGameModule module, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return new RegistryGameStatus(module, RegistryGameState.NotDetected, null, "未检测到注册表路径");
        }
        if (value is not string configuredPath)
        {
            return new RegistryGameStatus(module, RegistryGameState.InvalidPath, null, "注册表中的游戏路径不是文本");
        }

        try
        {
            var resolved = ResolveForModule(module, configuredPath);
            return new RegistryGameStatus(module, RegistryGameState.Detected, resolved, null);
        }
        catch (Exception exception)
            when (exception
                    is ArgumentException
                        or IOException
                        or InvalidDataException
                        or SecurityException
                        or UnauthorizedAccessException
            )
        {
            ApplicationLog.WriteDebug(
                $"{module.Descriptor.DisplayName} 注册表路径无效：{exception}",
                writeToConsole: false
            );
            return new RegistryGameStatus(module, RegistryGameState.InvalidPath, null, exception.Message);
        }
    }

    public static ResolvedGame Resolve(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        var fullPath = ConfiguredPath.Resolve(configuredPath);
        if (File.Exists(fullPath))
        {
            var module = GameRegistry.ByExecutableName(Path.GetFileName(fullPath));
            if (module is null)
            {
                throw new InvalidDataException(
                    $"请选择游戏本体 {string.Join(" 或 ", GameRegistry.All.Select(static game => game.Descriptor.ExecutableName))}"
                );
            }

            return new ResolvedGame(module, fullPath);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("指定的游戏目录或 EXE 不存在", fullPath);
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
