using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Zeitlind.Runtime;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.Desktop;

public partial class AboutWindow : Window
{
    internal AboutWindow(string? lastError)
    {
        InitializeComponent();
        BuildText.Text = $"Zeitlind v{ApplicationBuildInfo.Version} · {ApplicationBuildInfo.Configuration}";
        CopyrightText.Text = ApplicationBuildInfo.Copyright;
        RepositoryText.Text = ApplicationBuildInfo.RepositoryUrl;

        var logPath = ApplicationLog.CurrentFilePath;
        LogPathText.Text = logPath ?? "本次运行未创建日志文件。";
        if (logPath is not null)
        {
            OpenLogButton.IsEnabled = true;
            OpenLogButton.Click += (_, _) => OpenTarget(logPath, "无法打开日志");
        }

        if (lastError is null)
        {
            FeedbackText.Text = "本次运行暂无错误可复制。";
            return;
        }

        CopyErrorButton.IsEnabled = true;
        CopyErrorButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText($"{BuildText.Text}{Environment.NewLine}{lastError}");
                FeedbackText.Text = "已复制最近一次错误。";
            }
            catch (ExternalException)
            {
                FeedbackText.Text = "无法写入剪贴板，请重试。";
            }
        };
    }

    private void Repository_Click(object sender, RoutedEventArgs e) =>
        OpenTarget(ApplicationBuildInfo.RepositoryUrl, "无法打开原始仓库");

    private void OpenTarget(string target, string failureMessage)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Win32Exception exception)
        {
            FeedbackText.Text = $"{failureMessage}{Environment.NewLine}{exception.Message}";
        }
    }
}
