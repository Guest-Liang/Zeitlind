namespace Zeitlind.Hook.Common;

public sealed unsafe class InlineHook
{
    private const int AbsoluteJumpSize = 14;

    private readonly byte[] _originalBytes;
    private readonly nint _target;
    private readonly int _patchSize;
    private int _active;

    private InlineHook(nint target, int patchSize, byte[] originalBytes, nint trampoline)
    {
        _target = target;
        _patchSize = patchSize;
        _originalBytes = originalBytes;
        Trampoline = trampoline;
    }

    public nint Trampoline { get; }

    public static InlineHook Prepare(ParserLocation location)
    {
        if (location.Address == 0 || location.PatchSize < AbsoluteJumpSize)
        {
            throw new ArgumentException("Hook 位置或可覆盖入口长度无效", nameof(location));
        }

        var target = location.Address;
        var patchSize = location.PatchSize;
        var originalBytes = GC.AllocateUninitializedArray<byte>(patchSize);
        fixed (byte* destination = originalBytes)
        {
            Buffer.MemoryCopy((void*)target, destination, (nuint)patchSize, (nuint)patchSize);
        }

        var trampolineSize = checked(patchSize + AbsoluteJumpSize);
        var trampoline = NativeMethods.VirtualAlloc(
            0,
            (nuint)trampolineSize,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            NativeMethods.PageExecuteReadWrite
        );
        if (trampoline == 0)
        {
            throw new InvalidOperationException("VirtualAlloc 无法创建 Hook trampoline");
        }

        Buffer.MemoryCopy((void*)target, (void*)trampoline, (nuint)patchSize, (nuint)patchSize);
        WriteAbsoluteJump((byte*)trampoline + patchSize, target + patchSize);
        return new InlineHook(target, patchSize, originalBytes, trampoline);
    }

    public void Activate(nint detour)
    {
        if (detour == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(detour));
        }

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Inline Hook 已激活");
        }

        var patchSize = (nuint)_patchSize;
        var patched = false;
        try
        {
            if (
                !NativeMethods.VirtualProtect(
                    _target,
                    patchSize,
                    NativeMethods.PageExecuteReadWrite,
                    out var oldProtection
                )
            )
            {
                throw new InvalidOperationException("VirtualProtect 无法修改目标函数");
            }

            try
            {
                WriteAbsoluteJump((byte*)_target, detour);
                for (var index = AbsoluteJumpSize; index < _patchSize; index++)
                {
                    *((byte*)_target + index) = 0x90;
                }

                patched = true;
            }
            finally
            {
                _ = NativeMethods.VirtualProtect(_target, patchSize, oldProtection, out _);
            }

            if (!NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), _target, patchSize))
            {
                throw new InvalidOperationException("FlushInstructionCache 失败");
            }
        }
        catch
        {
            if (patched)
            {
                RestoreOriginalBytes();
            }

            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    public void Restore()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        RestoreOriginalBytes();
    }

    private void RestoreOriginalBytes()
    {
        var patchSize = (nuint)_patchSize;
        if (
            !NativeMethods.VirtualProtect(_target, patchSize, NativeMethods.PageExecuteReadWrite, out var oldProtection)
        )
        {
            return;
        }

        try
        {
            fixed (byte* source = _originalBytes)
            {
                Buffer.MemoryCopy(source, (void*)_target, patchSize, patchSize);
            }
        }
        finally
        {
            _ = NativeMethods.VirtualProtect(_target, patchSize, oldProtection, out _);
            _ = NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), _target, patchSize);
        }
    }

    private static void WriteAbsoluteJump(byte* destination, nint target)
    {
        *(ushort*)destination = 0x25FF;
        *(uint*)(destination + 2) = 0;
        *(nint*)(destination + 6) = target;
    }
}
