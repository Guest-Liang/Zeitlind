using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Zeitlind.App.Infrastructure;

internal static class ElevationManager
{
    private const int OperationCanceledError = 1223;

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchAsAdministrator(string gameExecutablePath, ExportTarget? target, string? outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameExecutablePath);

        var executablePath =
            Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 Zeitlind 可执行文件路径");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--game");
        startInfo.ArgumentList.Add(Path.GetFullPath(gameExecutablePath));
        if (target is not null)
        {
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add(ExportSelectionFlow.ToCliValue(target.Value));
        }

        if (outputDirectory is not null)
        {
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(Path.GetFullPath(outputDirectory));
        }
        ApplicationLog.WriteInfo(
            $"准备请求管理员权限：程序 {executablePath}；"
                + $"工作目录 {startInfo.WorkingDirectory}；"
                + $"游戏 {Path.GetFullPath(gameExecutablePath)}",
            writeToConsole: false
        );

        try
        {
            using var process =
                Process.Start(startInfo) ?? throw new InvalidOperationException("Windows 没有启动管理员权限实例");
            ApplicationLog.WriteDebug($"管理员权限实例已创建：PID {process.Id}", writeToConsole: false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == OperationCanceledError)
        {
            throw new OperationCanceledException("用户取消了管理员权限请求", exception);
        }
    }
}
