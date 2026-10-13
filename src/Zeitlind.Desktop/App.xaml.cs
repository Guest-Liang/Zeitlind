using System.Windows;
using System.Windows.Threading;
using Zeitlind.Runtime;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.Desktop;

public partial class App : Application
{
    private ApplicationLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplicationLog.ConsoleEnabled = false;
        _log = ApplicationLog.TryStart();
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                ApplicationLog.WriteException("未处理异常", exception);
            }
        };
        if (!ExportService.IsAdministrator())
        {
            MessageBox.Show(
                "请直接运行发布的 Zeitlind EXE，并允许管理员权限请求。",
                "Zeitlind",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ApplicationLog.WriteException("界面异常", e.Exception);
        MessageBox.Show(
            $"程序遇到错误，即将退出。\n{e.Exception.Message}",
            "Zeitlind",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
        e.Handled = true;
        Shutdown(1);
    }
}
