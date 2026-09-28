namespace Zeitlind.Hook.Common;

public sealed class PacketHookState
{
    private InlineHook? _inlineHook;
    private int _installed;

    public PacketHookInstallation WaitForModuleAndInstall(
        TimeSpan timeout,
        uint headMagic,
        uint tailMagic,
        nint detour,
        Action<nint> assignOriginal,
        string moduleName = "GameAssembly.dll"
    )
    {
        var located = PacketHookLocator.WaitForParser(timeout, headMagic, tailMagic, moduleName);
        Install(located.Parser, detour, assignOriginal);
        return new PacketHookInstallation(located.ModuleBase, located.Parser.Rva);
    }

    public void Install(ParserLocation location, nint detour, Action<nint> assignOriginal)
    {
        ArgumentNullException.ThrowIfNull(assignOriginal);
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
        {
            return;
        }

        InlineHook? hook = null;
        try
        {
            hook = InlineHook.Prepare(location);
            _inlineHook = hook;
            assignOriginal(hook.Trampoline);
            hook.Activate(detour);
        }
        catch (Exception installException)
        {
            try
            {
                hook?.Restore();
                Interlocked.Exchange(ref _inlineHook, null);
                Interlocked.Exchange(ref _installed, 0);
            }
            catch (Exception restoreException)
            {
                throw new AggregateException(
                    "安装 Packet Hook 失败，且回滚目标函数也失败",
                    installException,
                    restoreException
                );
            }

            throw;
        }
    }

    public void Uninstall()
    {
        if (Volatile.Read(ref _installed) == 0)
        {
            return;
        }

        Volatile.Read(ref _inlineHook)?.Restore();
        Interlocked.Exchange(ref _inlineHook, null);
        Interlocked.Exchange(ref _installed, 0);
    }
}
