using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace Zeitlind.App.Infrastructure;

internal enum ApplicationLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

internal sealed class ApplicationLog : IDisposable
{
    private static readonly object CurrentGate = new();
    private static ApplicationLog? _current;

    private readonly LogSink _sink;
    private bool _disposed;

    private ApplicationLog(string filePath, LogSink sink)
    {
        FilePath = filePath;
        _sink = sink;
    }

    public string FilePath { get; }

    public static string? CurrentFilePath
    {
        get
        {
            lock (CurrentGate)
            {
                return _current?.FilePath;
            }
        }
    }

    public static ApplicationLog? TryStart()
    {
        var localDate = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = $"Zeitlind-{localDate}.log";
        var filePath = Path.Combine(AppContext.BaseDirectory, fileName);
        FileStream? stream = null;
        LogSink? sink = null;

        try
        {
            stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            sink = new LogSink(writer);
            var log = new ApplicationLog(filePath, sink);
            log.WriteSessionHeader();

            lock (CurrentGate)
            {
                _current = log;
            }

            stream = null;
            sink = null;
            return log;
        }
        catch (Exception exception)
        {
            sink?.Dispose();
            stream?.Dispose();
            Console.Error.WriteLine($"警告：无法创建日志文件 {filePath}：{exception.Message}");
            return null;
        }
    }

    public static void WriteInfo(string message, bool writeToConsole = true)
    {
        Write(ApplicationLogLevel.Info, message, writeToConsole, useErrorStream: false);
    }

    public static void WriteWarning(string message, bool writeToConsole = true)
    {
        Write(ApplicationLogLevel.Warning, message, writeToConsole, useErrorStream: true);
    }

    public static void WriteError(string message, bool writeToConsole = true)
    {
        Write(ApplicationLogLevel.Error, message, writeToConsole, useErrorStream: true);
    }

    [Conditional("DEBUG")]
    public static void WriteDebug(string message, bool writeToConsole = false)
    {
        Write(ApplicationLogLevel.Debug, message, writeToConsole, useErrorStream: false);
    }

    public static void WriteException(string context, Exception exception)
    {
        WriteException(ApplicationLogLevel.Error, context, exception);
    }

    public static void WriteWarningException(string context, Exception exception)
    {
        WriteException(ApplicationLogLevel.Warning, context, exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sink.Write(ApplicationLogLevel.Info, "日志会话结束");

        lock (CurrentGate)
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        }

        _sink.Dispose();
    }

    private static void Write(ApplicationLogLevel level, string message, bool writeToConsole, bool useErrorStream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        lock (CurrentGate)
        {
            _current?._sink.Write(level, message);
        }

        if (!writeToConsole)
        {
            return;
        }

        var console = useErrorStream ? Console.Error : Console.Out;
        console.WriteLine(message);
    }

    private static void WriteException(ApplicationLogLevel level, string context, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(exception);

        Write(level, $"{context}{Environment.NewLine}{exception}", false, false);
    }

    private void WriteSessionHeader()
    {
        var localNow = DateTimeOffset.Now;
        var localTimeZone = TimeZoneInfo.Local;
        var localOffset = localNow.ToString("zzz", CultureInfo.InvariantCulture);
        var currentCulture = DisplayCulture(CultureInfo.CurrentCulture);
        var currentUiCulture = DisplayCulture(CultureInfo.CurrentUICulture);
        var privilege = TryGetPrivilegeDescription();

        _sink.Write(ApplicationLogLevel.Info, "Zeitlind 日志会话开始");
        _sink.Write(
            ApplicationLogLevel.Info,
            $"软件版本：{ApplicationBuildInfo.Version}；构建配置：{ApplicationBuildInfo.Configuration}"
        );
        _sink.Write(ApplicationLogLevel.Info, $"可执行文件：{Environment.ProcessPath ?? "unknown"}");
        _sink.Write(ApplicationLogLevel.Info, $"程序目录：{AppContext.BaseDirectory}");
        _sink.Write(ApplicationLogLevel.Info, $"当前工作目录：{Environment.CurrentDirectory}");
        _sink.Write(
            ApplicationLogLevel.Info,
            $"进程：PID {Environment.ProcessId}；权限：{privilege}；64 位进程：{Environment.Is64BitProcess}"
        );
        _sink.Write(
            ApplicationLogLevel.Info,
            $"操作系统：{RuntimeInformation.OSDescription}；"
                + $"版本：{Environment.OSVersion.VersionString}；"
                + $"架构：{RuntimeInformation.OSArchitecture}；"
                + $"64 位系统：{Environment.Is64BitOperatingSystem}"
        );
        _sink.Write(
            ApplicationLogLevel.Info,
            $"运行时：{RuntimeInformation.FrameworkDescription}；"
                + $"运行时版本：{Environment.Version}；"
                + $"进程架构：{RuntimeInformation.ProcessArchitecture}；"
                + $"Server GC：{GCSettings.IsServerGC}；"
                + $"逻辑处理器：{Environment.ProcessorCount}"
        );
        _sink.Write(
            ApplicationLogLevel.Info,
            $"区域：{currentCulture}；界面区域：{currentUiCulture}；"
                + $"时区：{localTimeZone.Id}；UTC 偏移：{localOffset}"
        );
        _sink.Write(
            ApplicationLogLevel.Info,
            $"控制台重定向：输入 {Console.IsInputRedirected}，"
                + $"输出 {Console.IsOutputRedirected}，错误 {Console.IsErrorRedirected}"
        );
    }

    private static string DisplayCulture(CultureInfo culture)
    {
        return string.IsNullOrEmpty(culture.Name) ? "Invariant" : culture.Name;
    }

    private static string TryGetPrivilegeDescription()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "非 Windows";
        }

        try
        {
            return ElevationManager.IsAdministrator() ? "管理员" : "普通用户";
        }
        catch (Exception)
        {
            return "无法确定";
        }
    }

    private sealed class LogSink : IDisposable
    {
        private readonly object _gate = new();
        private readonly TextWriter _writer;
        private bool _available = true;

        public LogSink(TextWriter writer)
        {
            _writer = writer;
        }

        public void Write(ApplicationLogLevel level, string message)
        {
            lock (_gate)
            {
                if (!_available)
                {
                    return;
                }

                try
                {
                    using var reader = new StringReader(message);
                    while (reader.ReadLine() is { } line)
                    {
                        var timestamp = DateTimeOffset.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff zzz",
                            CultureInfo.InvariantCulture
                        );
                        _writer.WriteLine($"[{timestamp}] [{LevelName(level)}] {line}");
                    }
                }
                catch (Exception)
                {
                    // Logging must never prevent an achievement export.
                    _available = false;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _available = false;
                try
                {
                    _writer.Dispose();
                }
                catch (Exception)
                {
                    // A final flush failure must not change the app result.
                }
            }
        }

        private static string LevelName(ApplicationLogLevel level)
        {
            return level switch
            {
                ApplicationLogLevel.Debug => "DEBUG",
                ApplicationLogLevel.Info => "INFO",
                ApplicationLogLevel.Warning => "WARN",
                ApplicationLogLevel.Error => "ERROR",
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未知日志级别"),
            };
        }
    }
}
