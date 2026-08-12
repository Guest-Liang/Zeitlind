using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zeitlind.App.Infrastructure;

internal sealed class SuspendedGameProcess : IDisposable
{
    private nint _processHandle;
    private nint _mainThreadHandle;
    private nint _jobHandle;
    private bool _resumed;
    private bool _disposed;

    private SuspendedGameProcess(int processId, nint processHandle, nint mainThreadHandle, nint jobHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _mainThreadHandle = mainThreadHandle;
        _jobHandle = jobHandle;
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

        var jobHandle = CreateKillOnCloseJob();
        var processInformation = default(NativeMethods.ProcessInformation);
        try
        {
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

            if (!NativeMethods.AssignProcessToJobObject(jobHandle, processInformation.Process))
            {
                throw NewWin32Exception("无法把游戏进程加入清理 Job Object");
            }

            var game = new SuspendedGameProcess(
                checked((int)processInformation.ProcessId),
                processInformation.Process,
                processInformation.Thread,
                jobHandle
            );
            jobHandle = 0;
            ApplicationLog.WriteInfo(
                $"游戏进程已以挂起状态启动并加入清理 Job Object：PID {game.ProcessId}；"
                    + $"程序 {fullPath}；工作目录 {workingDirectory}",
                writeToConsole: false
            );
            return game;
        }
        catch
        {
            if (processInformation.Process != 0)
            {
                _ = NativeMethods.TerminateProcess(processInformation.Process, 1);
            }

            CloseHandle(ref processInformation.Thread);
            CloseHandle(ref processInformation.Process);
            CloseHandle(ref jobHandle);
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

        var terminated =
            _jobHandle != 0
                ? NativeMethods.TerminateJobObject(_jobHandle, exitCode)
                : NativeMethods.TerminateProcess(_processHandle, exitCode);
        if (!terminated)
        {
            throw NewWin32Exception("强制关闭游戏进程及其子进程失败");
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

    public async Task<bool> TryCloseGracefullyAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "等待时间必须大于零");
        }

        if (HasExited())
        {
            return true;
        }

        bool closeRequested;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            closeRequested = process.CloseMainWindow();
        }
        catch (ArgumentException) when (HasExited())
        {
            return true;
        }

        if (!closeRequested)
        {
            return false;
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            await WaitForExitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return false;
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

        // Closing a KILL_ON_JOB_CLOSE job is the final fallback if the explicit termination above failed.
        CloseHandle(ref _jobHandle);

        CloseHandle(ref _mainThreadHandle);
        CloseHandle(ref _processHandle);
    }

    private bool HasExited()
    {
        var waitResult = NativeMethods.WaitForSingleObject(_processHandle, 0);
        return waitResult switch
        {
            NativeMethods.WaitObject0 => true,
            NativeMethods.WaitTimeout => false,
            NativeMethods.WaitFailed => throw NewWin32Exception("检查游戏进程状态失败"),
            _ => throw new InvalidOperationException($"检查游戏进程返回未知状态 0x{waitResult:X8}"),
        };
    }

    private static nint CreateKillOnCloseJob()
    {
        var jobHandle = NativeMethods.CreateJobObject(0, null);
        if (jobHandle == 0)
        {
            throw NewWin32Exception("创建游戏清理 Job Object 失败");
        }

        try
        {
            var information = new NativeMethods.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
                {
                    LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
                },
            };
            if (
                !NativeMethods.SetInformationJobObject(
                    jobHandle,
                    NativeMethods.JobObjectExtendedLimitInformationClass,
                    ref information,
                    checked((uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>())
                )
            )
            {
                throw NewWin32Exception("配置游戏清理 Job Object 失败");
            }

            return jobHandle;
        }
        catch
        {
            CloseHandle(ref jobHandle);
            throw;
        }
    }

    private static void CloseHandle(ref nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        _ = NativeMethods.CloseHandle(handle);
        handle = 0;
    }

    internal static Win32Exception NewWin32Exception(string operation)
    {
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}（Win32 {error}）");
    }
}
