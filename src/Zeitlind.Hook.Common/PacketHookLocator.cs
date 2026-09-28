namespace Zeitlind.Hook.Common;

public static class PacketHookLocator
{
    public static (nint ModuleBase, ParserLocation Parser) WaitForParser(
        TimeSpan timeout,
        uint headMagic,
        uint tailMagic,
        string moduleName = "GameAssembly.dll"
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        var moduleBase = LoadedModule.WaitFor(moduleName, timeout);
        var parser = ParserLocator.Locate(moduleBase, moduleName, headMagic, tailMagic);
        return (moduleBase, parser);
    }
}
