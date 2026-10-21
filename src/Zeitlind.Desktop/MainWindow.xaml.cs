using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Zeitlind.Core.Games;
using Zeitlind.Runtime;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.Desktop;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCancellation;
    private AchievementCapture? _capture;
    private string? _manualExecutable;
    private string? _resultDirectory;
    private bool _closeAfterOperation;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{ApplicationBuildInfo.Version}";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshGames();
        if (ApplicationLog.CurrentFilePath is null)
        {
            SetStatus("准备就绪 · 日志不可用", "程序目录不可写，无法创建运行日志。请选择游戏开始导出。", isError: true);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshGames(showStatus: true);

    private bool RefreshGames(bool showStatus = false)
    {
        if (_operationCancellation is not null)
        {
            return false;
        }
        try
        {
            var statuses = ExportService.DiscoverGames();
            GamesList.ItemsSource = statuses.Select(static status => new GameCard(status)).ToArray();
            var invalidPathCount = statuses.Count(static status => status.State == RegistryGameState.InvalidPath);
            var readFailedCount = statuses.Count(static status => status.State == RegistryGameState.ReadFailed);
            var hasIssues = invalidPathCount + readFailedCount != 0;
            if (showStatus || hasIssues)
            {
                var detectedCount = statuses.Count(static status => status.IsDetected);
                var missingCount = statuses.Count(static status => status.State == RegistryGameState.NotDetected);
                var parts = new List<string> { $"检测到 {detectedCount} / {statuses.Length} 款游戏" };
                if (missingCount != 0)
                {
                    parts.Add($"{missingCount} 款未检测到安装");
                }
                if (invalidPathCount != 0)
                {
                    parts.Add($"{invalidPathCount} 项路径无效");
                }
                if (readFailedCount != 0)
                {
                    parts.Add($"{readFailedCount} 项读取失败");
                }
                var hint = hasIssues
                    ? "请查看对应游戏卡片的原因，也可手动选择 EXE。"
                    : "未检测到的游戏可手动选择 EXE。";
                SetStatus(
                    hasIssues ? "注册表读取完成 · 请检查提示" : "注册表读取完成",
                    $"已重新读取注册表并更新游戏路径，{string.Join("，", parts)}。\n"
                        + $"{hint}更新时间：{DateTime.Now:HH:mm:ss}。",
                    isError: hasIssues
                );
            }
            return !hasIssues;
        }
        catch (Exception exception)
        {
            ShowError("读取注册表失败", exception);
            return false;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not null || _capture is not null)
        {
            return;
        }
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择游戏本体：ZenlessZoneZero.exe / StarRail.exe / YuanShen.exe",
                Filter = "游戏本体 EXE|ZenlessZoneZero.exe;StarRail.exe;YuanShen.exe|可执行文件 (*.exe)|*.exe",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                DereferenceLinks = true,
            };
            var initialPath =
                _manualExecutable
                ?? (GamesList.ItemsSource as GameCard[])
                    ?.FirstOrDefault(static card => card.IsAvailable)
                    ?.ExecutablePath;
            if (initialPath is not null)
            {
                dialog.InitialDirectory = Path.GetDirectoryName(initialPath);
            }
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var selection = ExportService.ResolveInstallation(dialog.FileName);
            _manualExecutable = selection.ExecutablePath;
            ManualTitle.Text = selection.Module.Descriptor.DisplayName;
            ManualDetail.Text = selection.ExecutablePath;
            ManualExportButton.Visibility = Visibility.Visible;
            SetStatus(
                "游戏已选择",
                $"{selection.Module.Descriptor.DisplayName} · {selection.Version}，点击右侧“导出成就”开始。"
            );
        }
        catch (Exception exception)
        {
            ShowError("无法选择游戏", exception);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameCard { ExecutablePath: { } path } })
        {
            await CaptureAsync(path);
        }
    }

    private async void ManualExport_Click(object sender, RoutedEventArgs e)
    {
        if (_manualExecutable is { } path)
        {
            await CaptureAsync(path);
        }
    }

    private async Task CaptureAsync(string executablePath)
    {
        if (_operationCancellation is not null || _capture is not null)
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _resultDirectory = null;
        OpenResultButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        SetStatus("正在准备", "正在检查游戏安装与运行状态…");
        var progress = new Progress<ExportProgress>(update =>
        {
            // Queued dispatcher updates must not overwrite cancellation or a finished result.
            if (_operationCancellation == cancellation && !cancellation.IsCancellationRequested)
            {
                SetStatus("正在导出", update.Message);
            }
        });
        try
        {
            _capture = await Task.Run(() => ExportService.CaptureAsync(executablePath, progress, cancellation.Token));
            FormatSelector.ItemsSource = _capture.Formats;
            FormatSelector.SelectedIndex = 0;
            SavePanel.Visibility = Visibility.Visible;
            var uid = _capture.Uid?.ToString() ?? "未确认（需要 UID 的字段将保存为 null）";
            SetStatus(
                "成就读取完成",
                $"{_capture.GameName} · UID {uid}\n共 {_capture.RecordCount} 条成就，已完成 {_capture.CompletedCount} 条。请选择格式并保存。"
            );
            SavePanel.BringIntoView();
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消", "本次采集已结束，临时资源已清理，没有写入成就文件。");
        }
        catch (Exception exception)
        {
            ShowError("导出失败", exception);
        }
        finally
        {
            FinishOperation();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (
            _operationCancellation is not null
            || _capture is not { } capture
            || FormatSelector.SelectedItem is not ExportFormatOption option
        )
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        SetBusy(true);
        SetStatus("正在保存", "正在写入成就文件…");
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Exports");
            var result = await Task.Run(() => capture.SaveAsync(option.Target, directory, cancellation.Token));
            _resultDirectory = Path.GetDirectoryName(result.Path);
            OpenResultButton.Visibility = Visibility.Visible;
            SetStatus("导出完成", $"{result.DisplayName}\n{result.Path}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("保存已取消", "成就数据仍保留在本窗口，可以重新保存。");
        }
        catch (Exception exception)
        {
            ShowError("保存失败 · 可以重试", exception);
        }
        finally
        {
            FinishOperation();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelOperation();

    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        SetStatus("正在取消", "正在结束本次操作并清理临时资源，请稍候…");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_operationCancellation is null)
        {
            return;
        }
        e.Cancel = true;
        _closeAfterOperation = true;
        CancelOperation();
    }

    private void FinishOperation()
    {
        _operationCancellation = null;
        SetBusy(false);
        if (_closeAfterOperation)
        {
            Close();
        }
    }

    private void SetBusy(bool busy)
    {
        GameActions.IsEnabled = !busy && _capture is null;
        SavePanel.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        ExportProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not null)
        {
            return;
        }
        _capture = null;
        _resultDirectory = null;
        SavePanel.Visibility = Visibility.Collapsed;
        SetBusy(false);
        if (RefreshGames())
        {
            SetStatus("准备就绪", "选择游戏，开始下一次成就导出。");
        }
        GameActions.BringIntoView();
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (_resultDirectory is not { } directory)
        {
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError("无法打开输出目录", exception);
        }
    }

    private void ShowError(string title, Exception exception)
    {
        ApplicationLog.WriteException(title, exception);
        var logHint = ApplicationLog.CurrentFilePath is { } path ? $"\n详细信息：{path}" : "";
        SetStatus(title, exception.Message + logHint, isError: true);
    }

    private void SetStatus(string title, string detail, bool isError = false)
    {
        StatusTitle.Text = title;
        StatusTitle.Foreground = isError ? Brushes.Firebrick : (Brush)FindResource("TextBrush");
        StatusDetail.Text = detail;
    }
}

internal sealed class GameCard(RegistryGameStatus status)
{
    public string Name => status.Module.Descriptor.DisplayName;
    public string? ExecutablePath => status.ExecutablePath;
    public bool IsAvailable => status.IsDetected;
    public string Detail => ExecutablePath ?? $"{status.Description} · 可手动选择";
    public string ExportLabel => $"导出{Name}成就";
    public string Badge => status.Module.Descriptor.Kind.ToString();
    public string BadgeBackground =>
        status.Module.Descriptor.Kind switch
        {
            GameKind.ZZZ => "#FFF0DE",
            GameKind.HSR => "#EDE9FB",
            _ => "#E6F1FD",
        };
    public string BadgeForeground =>
        status.Module.Descriptor.Kind switch
        {
            GameKind.ZZZ => "#9A570A",
            GameKind.HSR => "#6550A3",
            _ => "#32679D",
        };
}
