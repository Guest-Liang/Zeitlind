using System.Diagnostics;
using Zeitlind.App.Infrastructure;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App;

internal static class ExporterApplication
{
    internal const int UserRequestedExitCode = 4;
    internal const int RelaunchedAsAdministratorExitCode = 5;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var options, out var argumentError))
        {
            ApplicationLog.WriteError(argumentError ?? "无法识别命令行参数");
            WriteUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            WriteUsage(writeAsError: false);
            return UserRequestedExitCode;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"Zeitlind {ApplicationBuildInfo.Version}");
            return UserRequestedExitCode;
        }

        ApplicationLog.WriteInfo("Zeitlind — 绝区零与崩坏：星穹铁道成就导出");
        ApplicationLog.WriteInfo("https://github.com/Guest-Liang/Zeitlind");
        Console.WriteLine();
        ApplicationLog.WriteWarning(
            """
            免责声明：Zeitlind 是非官方第三方工具，运行时会向所选游戏进程加载临时 Hook，
            可能违反游戏规则或被反作弊系统识别，并可能导致账号限制或封禁。
            使用者自行判断并承担全部风险，项目作者及贡献者不对账号处罚或其他损失负责。
            若无法接受上述风险，请立即关闭本程序。
            """
        );
        if (ApplicationLog.CurrentFilePath is { } logPath)
        {
            ApplicationLog.WriteInfo($"运行日志：{logPath}");
        }
        ApplicationLog.WriteWarning(
            "隐私提示：日志会记录游戏/导出路径和 UID，且不会自动删除；分享日志前请先检查其中的个人信息。"
        );
        Console.WriteLine();

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            ApplicationLog.WriteError("Zeitlind 只支持 Windows x64");
            return 2;
        }

        try
        {
            EmbeddedHook.CleanLegacyDirectories();
            return await ExportAsync(options);
        }
        catch (OperationCanceledException)
        {
            ApplicationLog.WriteInfo("用户取消导出；没有导出成就文件", writeToConsole: false);
            ApplicationLog.WriteInfo("已取消");
            return 3;
        }
        catch (Exception exception)
        {
            ApplicationLog.WriteException("导出失败", exception);
            Console.Error.WriteLine();
            ApplicationLog.WriteError($"导出失败：{exception.Message}");
            ApplicationLog.WriteError("没有导出不完整的成就文件");
            if (ApplicationLog.CurrentFilePath is { } failureLogPath)
            {
                ApplicationLog.WriteError($"详细信息已写入：{failureLogPath}");
            }

            return 1;
        }
    }

    private static async Task<int> ExportAsync(ApplicationOptions options)
    {
        var outputDirectory = options.OutputDirectory is null ? null : ConfiguredPath.Resolve(options.OutputDirectory);
        var selection = GameSelectionFlow.Select(options.GamePath);
        if (selection is null)
        {
            return UserRequestedExitCode;
        }

        EnsureGameIsNotRunning(selection);
        if (!ElevationManager.IsAdministrator())
        {
            ApplicationLog.WriteInfo($"游戏：{selection.Module.Descriptor.DisplayName}");
            ApplicationLog.WriteInfo($"游戏路径：{selection.ExecutablePath}");
            ApplicationLog.WriteInfo($"游戏版本：{selection.Version}");
            ApplicationLog.WriteInfo("游戏已确认，正在申请管理员权限...");
            ElevationManager.RelaunchAsAdministrator(selection.ExecutablePath, options.ExportTarget, outputDirectory);
            return RelaunchedAsAdministratorExitCode;
        }

        using var extractedHook =
            EmbeddedHook.TryExtract(selection.Module)
            ?? throw new InvalidOperationException(
                $"当前构建没有内嵌 {selection.Module.Descriptor.DisplayName} Hook DLL；请使用完整 Zeitlind"
            );
        var catalog = AchievementCatalog.LoadBundled(selection.Module.Descriptor.Kind);

        ApplicationLog.WriteInfo($"游戏：{selection.Module.Descriptor.DisplayName}");
        ApplicationLog.WriteInfo($"游戏路径：{selection.ExecutablePath}");
        ApplicationLog.WriteInfo($"游戏版本：{selection.Version}");
        ApplicationLog.WriteInfo($"成就元数据：{catalog.LatestVersion}（{catalog.Count} 项）");
        ApplicationLog.WriteInfo("正在启动游戏");

        return await AchievementExportSession.RunAsync(
            selection,
            extractedHook.Path,
            catalog,
            options.ExportTarget,
            outputDirectory
        );
    }

    private static bool TryParseArguments(string[] args, out ApplicationOptions options, out string? error)
    {
        string? gamePath = null;
        string? outputDirectory = null;
        ExportTarget? exportTarget = null;
        var showHelp = false;
        var showVersion = false;
        error = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--game":
                    if (!TryReadSingleValue(args, ref index, ref gamePath, "--game", out error))
                    {
                        options = default!;
                        return false;
                    }

                    break;

                case "--output":
                    if (!TryReadSingleValue(args, ref index, ref outputDirectory, "--output", out error))
                    {
                        options = default!;
                        return false;
                    }

                    break;

                case "--format":
                    if (exportTarget is not null)
                    {
                        error = "--format 不能重复指定";
                        options = default!;
                        return false;
                    }

                    if (++index >= args.Length || !TryParseFormat(args[index], out var parsedTarget))
                    {
                        error = "--format 必须是 backup、liyin 或 uiaf";
                        options = default!;
                        return false;
                    }

                    exportTarget = parsedTarget;
                    break;

                case "--help":
                case "-h":
                    showHelp = true;
                    break;

                case "--version":
                    showVersion = true;
                    break;

                default:
                    error = $"无法识别命令行参数：{argument}";
                    options = default!;
                    return false;
            }
        }

        if ((showHelp || showVersion) && args.Length != 1)
        {
            error = "--help 和 --version 必须单独使用";
            options = default!;
            return false;
        }

        options = new ApplicationOptions(gamePath, exportTarget, outputDirectory, showHelp, showVersion);
        return true;
    }

    private static bool TryReadSingleValue(
        string[] args,
        ref int index,
        ref string? destination,
        string option,
        out string? error
    )
    {
        error = null;
        if (destination is not null)
        {
            error = $"{option} 不能重复指定";
            return false;
        }

        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            error = $"{option} 后必须提供值";
            return false;
        }

        destination = args[index];
        return true;
    }

    private static bool TryParseFormat(string value, out ExportTarget target)
    {
        target = value.ToLowerInvariant() switch
        {
            "backup" => ExportTarget.AchievementBackup,
            "liyin" => ExportTarget.Liyin,
            "uiaf" => ExportTarget.UiafExperimental,
            _ => (ExportTarget)(-1),
        };
        return Enum.IsDefined(target);
    }

    private static void WriteUsage(bool writeAsError = true)
    {
        const string usage =
            "用法：Zeitlind.exe [--game \"游戏目录或 ZenlessZoneZero.exe/StarRail.exe\"] "
            + "[--format backup|liyin|uiaf] [--output \"输出目录\"]";
        if (writeAsError)
        {
            ApplicationLog.WriteError(usage);
        }
        else
        {
            Console.WriteLine(usage);
            Console.WriteLine("--game 通过 EXE 文件名自动识别游戏并选择对应 Hook；--output 不存在时会自动创建。");
        }
    }

    private static void EnsureGameIsNotRunning(GameSelection selection)
    {
        var descriptor = selection.Module.Descriptor;
        var processes = Process.GetProcessesByName(descriptor.ProcessName);
        try
        {
            if (processes.Length != 0)
            {
                throw new InvalidOperationException(
                    $"检测到{descriptor.DisplayName}已经在运行，请先退出游戏，再运行 Zeitlind"
                );
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

internal sealed record ApplicationOptions(
    string? GamePath,
    ExportTarget? ExportTarget,
    string? OutputDirectory,
    bool ShowHelp,
    bool ShowVersion
);
