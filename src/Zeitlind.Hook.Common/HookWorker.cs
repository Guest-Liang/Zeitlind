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

        try
        {
            work();
        }
        catch (OperationCanceledException)
        {
            // GameAssembly.dll 加载前的正常退出
        }
        catch (Exception exception)
        {
            reportError(exception.ToString());
        }
        finally
        {
            cleanup();
        }
    }
}

public sealed class HookHost
{
    private readonly HookFrameTransport transport;
    private readonly Func<TimeSpan, PacketHookInstallation> install;
    private readonly Action<PacketHookInstallation> run;
    private readonly Action uninstall;

    private int started;
    private Thread? worker;

    public HookHost(
        HookFrameTransport transport,
        Func<TimeSpan, PacketHookInstallation> install,
        Action<PacketHookInstallation> run,
        Action uninstall
    )
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.install = install ?? throw new ArgumentNullException(nameof(install));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.uninstall = uninstall ?? throw new ArgumentNullException(nameof(uninstall));
    }

    public int Start(nint bootstrapContext)
    {
        _ = bootstrapContext;
        return HookWorker.StartOnce(ref started, ref worker, Execute);
    }

    private void Execute()
    {
        HookWorker.Run(RunInstalledHook, transport.TrySendError, Cleanup);
    }

    private void RunInstalledHook()
    {
        transport.Connect(Environment.ProcessId);
        var installation = install(TimeSpan.FromMinutes(2));
        run(installation);
    }

    private void Cleanup()
    {
        transport.RequestShutdown();
        uninstall();
        transport.Disconnect();
    }
}
