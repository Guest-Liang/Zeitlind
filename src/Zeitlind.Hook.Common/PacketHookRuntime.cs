namespace Zeitlind.Hook.Common;

public readonly record struct PacketHookInstallation(nint ModuleBase, uint ParserRva);

public sealed class PacketHookState
{
    private InlineHook? _inlineHook;
    private int _installed;

    public PacketHookInstallation WaitForModuleAndInstall(
        TimeSpan timeout,
        uint headMagic,
        uint tailMagic,
        nint detour,
        Action<nint> assignOriginal
    )
    {
        var located = PacketHookLocator.WaitForParser(timeout, headMagic, tailMagic);
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

        try
        {
            var hook = InlineHook.Prepare(location);
            assignOriginal(hook.Trampoline);
            hook.Activate(detour);
            _inlineHook = hook;
        }
        catch
        {
            Interlocked.Exchange(ref _installed, 0);
            throw;
        }
    }

    public void Uninstall()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _inlineHook, null)?.Restore();
    }
}

public static class PacketHookLocator
{
    public static (nint ModuleBase, ParserLocation Parser) WaitForParser(
        TimeSpan timeout,
        uint headMagic,
        uint tailMagic
    )
    {
        var moduleBase = LoadedModule.WaitFor("GameAssembly.dll", timeout);
        var parser = ParserLocator.Locate(moduleBase, headMagic, tailMagic);
        return (moduleBase, parser);
    }
}
