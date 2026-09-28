namespace Zeitlind.Hook.Common;

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
        try
        {
            HookWorker.Run(RunInstalledHook, transport.TrySendError, Cleanup);
        }
        finally
        {
            transport.Disconnect();
        }
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
    }
}
