using System.ComponentModel;

namespace Zeitlind.App.Infrastructure;

internal sealed class SuspendedGameProcess : IDisposable
{
    private nint _processHandle;
    private nint _mainThreadHandle;
    private bool _resumed;
    private bool _disposed;

    private SuspendedGameProcess(int processId, nint processHandle, nint mainThreadHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _mainThreadHandle = mainThreadHandle;
    }

    public int ProcessId { get; }

    public nint ProcessHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _processHandle;
        }
    }

    public static unsafe SuspendedGameProcess Start(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var fullPath = Path.GetFullPath(executablePath);
        var workingDirectory =
            Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("无法确定游戏工作目录");
        var commandLine = $"\"{fullPath}\"".ToCharArray();
        Array.Resize(ref commandLine, commandLine.Length + 1);

        var startupInfo = new NativeMethods.StartupInfo { Size = checked((uint)sizeof(NativeMethods.StartupInfo)) };

        NativeMethods.ProcessInformation processInformation;
        fixed (char* commandLinePointer = commandLine)
        {
            if (
                !NativeMethods.CreateProcess(
                    fullPath,
                    commandLinePointer,
                    0,
                    0,
                    false,
                    NativeMethods.CreateSuspended,
                    0,
                    workingDirectory,
                    ref startupInfo,
                    out processInformation
                )
            )
            {
                throw NewWin32Exception("无法以挂起状态启动游戏进程");
            }
        }

        try
        {
            var game = new SuspendedGameProcess(
                checked((int)processInformation.ProcessId),
                processInformation.Process,
                processInformation.Thread
            );
            ApplicationLog.WriteInfo(
                $"游戏进程已以挂起状态启动：PID {game.ProcessId}；程序 {fullPath}；工作目录 {workingDirectory}",
                writeToConsole: false
            );
            return game;
        }
        catch
        {
            _ = NativeMethods.TerminateProcess(processInformation.Process, 1);
            _ = NativeMethods.CloseHandle(processInformation.Thread);
            _ = NativeMethods.CloseHandle(processInformation.Process);
            throw;
        }
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resumed)
        {
            return;
        }

        var previousCount = NativeMethods.ResumeThread(_mainThreadHandle);
        if (previousCount == uint.MaxValue)
        {
            throw NewWin32Exception("恢复游戏主线程失败");
        }

        _resumed = true;
        ApplicationLog.WriteInfo(
            $"游戏主线程已恢复：PID {ProcessId}；此前挂起计数 {previousCount}",
            writeToConsole: false
        );
    }

    public void Terminate(uint exitCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var currentState = NativeMethods.WaitForSingleObject(_processHandle, 0);
        if (currentState == NativeMethods.WaitObject0)
        {
            return;
        }

        if (currentState == NativeMethods.WaitFailed)
        {
            throw NewWin32Exception("检查游戏进程状态失败");
        }

        if (currentState != NativeMethods.WaitTimeout)
        {
            throw new InvalidOperationException($"检查游戏进程返回未知状态 0x{currentState:X8}");
        }

        if (!NativeMethods.TerminateProcess(_processHandle, exitCode))
        {
            throw NewWin32Exception("关闭游戏进程失败");
        }

        var waitResult = NativeMethods.WaitForSingleObject(_processHandle, 5_000);
        if (waitResult == NativeMethods.WaitTimeout)
        {
            throw new TimeoutException("等待游戏进程关闭超时");
        }

        if (waitResult == NativeMethods.WaitFailed)
        {
            throw NewWin32Exception("等待游戏进程关闭失败");
        }

        if (waitResult != NativeMethods.WaitObject0)
        {
            throw new InvalidOperationException($"等待游戏进程关闭返回未知状态 0x{waitResult:X8}");
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            var waitResult = NativeMethods.WaitForSingleObject(_processHandle, 0);
            if (waitResult == NativeMethods.WaitObject0)
            {
                return;
            }

            if (waitResult == NativeMethods.WaitFailed)
            {
                throw NewWin32Exception("等待游戏进程退出失败");
            }

            if (waitResult != NativeMethods.WaitTimeout)
            {
                throw new InvalidOperationException($"等待游戏进程返回未知状态 0x{waitResult:X8}");
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_processHandle != 0)
        {
            try
            {
                Terminate(1);
            }
            catch (Exception exception)
            {
                ApplicationLog.WriteWarningException("最终清理游戏进程失败", exception);
            }
        }

        _disposed = true;

        if (_mainThreadHandle != 0)
        {
            _ = NativeMethods.CloseHandle(_mainThreadHandle);
            _mainThreadHandle = 0;
        }

        if (_processHandle != 0)
        {
            _ = NativeMethods.CloseHandle(_processHandle);
            _processHandle = 0;
        }
    }

    internal static Win32Exception NewWin32Exception(string operation)
    {
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}（Win32 {error}）");
    }
}
