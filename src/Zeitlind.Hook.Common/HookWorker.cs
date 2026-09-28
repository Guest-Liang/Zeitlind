namespace Zeitlind.Hook.Common;

public static class HookWorker
{
    public static int StartOnce(ref int started, ref Thread? worker, ThreadStart run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return 1;
        }

        worker = new Thread(run) { IsBackground = true, Name = "Zeitlind Hook Worker" };
        worker.Start();
        return 0;
    }

    public static void Run(Action work, Action<string> reportError, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(reportError);
        ArgumentNullException.ThrowIfNull(cleanup);

        Exception? failure = null;
        try
        {
            work();
        }
        catch (OperationCanceledException)
        {
            // 目标模块加载前的正常退出
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            cleanup();
        }
        catch (Exception cleanupException)
        {
            failure = failure is null
                ? new InvalidOperationException("Hook 清理失败", cleanupException)
                : new AggregateException("Hook 执行和清理均失败", failure, cleanupException);
        }

        if (failure is not null)
        {
            reportError(failure.ToString());
        }
    }
}
