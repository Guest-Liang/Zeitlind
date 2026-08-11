using Zeitlind.App.Games;
using Zeitlind.App.Infrastructure;

namespace Zeitlind.App;

internal static class GameSelectionFlow
{
    public static GameSelection? Select(string? configuredGamePath)
    {
        if (configuredGamePath is not null)
        {
            ApplicationLog.WriteInfo("游戏路径来源：命令行 --game");
            return ResolveAndValidate(configuredGamePath);
        }

        var statuses = GameRegistry.All.Select(GameLocator.ReadRegistryStatus).ToArray();
        if (Console.IsInputRedirected)
        {
            var detected = statuses.Where(static status => status.ExecutablePath is not null).ToArray();
            return detected.Length switch
            {
                1 => Validate(detected[0].Module, detected[0].ExecutablePath!),
                0 => throw new FileNotFoundException(
                    "没有从注册表检测到受支持的游戏；请使用 --game 指定游戏目录或 EXE"
                ),
                _ => throw new InvalidOperationException(
                    "注册表检测到多个游戏；非交互启动时请使用 --game 明确指定目录或 EXE"
                ),
            };
        }

        var selectedOption = Array.FindIndex(statuses, static status => status.ExecutablePath is not null);
        if (selectedOption < 0)
        {
            selectedOption = statuses.Length;
        }
        while (true)
        {
            ApplicationLog.WriteInfo("请选择游戏（↑/↓ 选择，Enter 确认）：");
            var options = statuses
                .Select(static status => status.MenuText)
                .Append("使用资源管理器选择游戏 EXE（自动识别游戏）")
                .Append("退出 Zeitlind")
                .ToArray();
            var selected = ConsoleSelectionMenu.Read(
                options,
                selectedOption,
                CancellationToken.None,
                escapeSelection: options.Length - 1
            );
            selectedOption = selected;
            ApplicationLog.WriteInfo($"已选择：{options[selected]}");

            if (selected < statuses.Length)
            {
                var status = statuses[selected];
                if (status.ExecutablePath is null)
                {
                    ApplicationLog.WriteWarning(
                        $"{status.Module.Descriptor.DisplayName}：{status.Error ?? "没有注册表路径"}"
                    );
                    WaitForReturn();
                    continue;
                }

                try
                {
                    return Validate(status.Module, status.ExecutablePath);
                }
                catch (Exception exception) when (IsValidationException(exception))
                {
                    ApplicationLog.WriteWarning($"注册表中的游戏安装无效：{exception.Message}");
                    WaitForReturn();
                    statuses = GameRegistry.All.Select(GameLocator.ReadRegistryStatus).ToArray();
                    continue;
                }
            }

            if (selected == statuses.Length)
            {
                string? selectedPath;
                try
                {
                    selectedPath = GameExecutablePicker.Pick(
                        "Zeitlind 支持的",
                        "ZenlessZoneZero.exe 或 StarRail.exe",
                        statuses
                            .Select(static status => status.ExecutablePath)
                            .FirstOrDefault(static path => path is not null)
                    );
                }
                catch (IOException exception)
                {
                    ApplicationLog.WriteWarning($"无法打开文件选择窗口：{exception.Message}");
                    WaitForReturn();
                    continue;
                }

                if (selectedPath is null)
                {
                    ApplicationLog.WriteInfo("已取消文件选择，返回游戏菜单");
                    continue;
                }

                try
                {
                    return ResolveAndValidate(selectedPath);
                }
                catch (Exception exception) when (IsValidationException(exception))
                {
                    ApplicationLog.WriteWarning($"选择的游戏程序无效：{exception.Message}");
                    WaitForReturn();
                    continue;
                }
            }

            return null;
        }
    }

    private static GameSelection ResolveAndValidate(string configuredPath)
    {
        var resolved = GameLocator.Resolve(configuredPath);
        return Validate(resolved.Module, resolved.ExecutablePath);
    }

    private static GameSelection Validate(IGameModule module, string executablePath)
    {
        var version = module.ValidateInstallation(executablePath);
        return new GameSelection(module, executablePath, version);
    }

    private static bool IsValidationException(Exception exception)
    {
        return exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException;
    }

    private static void WaitForReturn()
    {
        Console.Write("按 Enter 返回游戏选择菜单...");
        _ = Console.ReadLine();
        Console.WriteLine();
    }
}

internal sealed record GameSelection(IGameModule Module, string ExecutablePath, string Version);
