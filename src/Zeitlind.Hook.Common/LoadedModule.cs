namespace Zeitlind.Hook.Common;

public static class LoadedModule
{
    public static nint WaitFor(string moduleName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var module = NativeMethods.GetModuleHandle(moduleName);
            if (module != 0)
            {
                return module;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"等待 {moduleName} 加载超时");
            }

            Thread.Sleep(25);
        }
    }
}
