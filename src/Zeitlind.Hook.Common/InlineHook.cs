using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zeitlind.Hook.Common;

public sealed unsafe class InlineHook
{
    private const int AbsoluteJumpSize = 14;

    private readonly byte[] _originalBytes;
    private readonly nint _target;
    private readonly int _patchSize;
    private int _active;
    private uint _originalProtection;
    private int _originalProtectionKnown;

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
            NativeMethods.PageReadWrite
        );
        if (trampoline == 0)
        {
            throw NewWin32Exception("VirtualAlloc 无法创建 Hook trampoline");
        }

        try
        {
            Buffer.MemoryCopy((void*)target, (void*)trampoline, (nuint)patchSize, (nuint)patchSize);
            WriteAbsoluteJump((byte*)trampoline + patchSize, target + patchSize);

            if (
                !NativeMethods.VirtualProtect(
                    trampoline,
                    (nuint)trampolineSize,
                    NativeMethods.PageExecuteRead,
                    out _
                )
            )
            {
                throw NewWin32Exception("无法把 Hook trampoline 切换为只读可执行内存");
            }

            if (
                !NativeMethods.FlushInstructionCache(
                    NativeMethods.GetCurrentProcess(),
                    trampoline,
                    (nuint)trampolineSize
                )
            )
            {
                throw NewWin32Exception("刷新 Hook trampoline 指令缓存失败");
            }

            return new InlineHook(target, patchSize, originalBytes, trampoline);
        }
        catch
        {
            _ = NativeMethods.VirtualFree(trampoline, 0, NativeMethods.MemRelease);
            throw;
        }
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
        uint originalProtection = 0;
        try
        {
            if (
                !NativeMethods.VirtualProtect(
                    _target,
                    patchSize,
                    NativeMethods.PageExecuteReadWrite,
                    out originalProtection
                )
            )
            {
                throw NewWin32Exception("VirtualProtect 无法修改目标函数");
            }

            _originalProtection = originalProtection;
            Volatile.Write(ref _originalProtectionKnown, 1);
            // From this point on, conservatively assume at least part of the entry may be modified.
            patched = true;
            WriteAbsoluteJump((byte*)_target, detour);
            for (var index = AbsoluteJumpSize; index < _patchSize; index++)
            {
                *((byte*)_target + index) = 0x90;
            }

            if (!NativeMethods.VirtualProtect(_target, patchSize, originalProtection, out _))
            {
                throw NewWin32Exception("恢复目标函数页保护失败");
            }

            if (!NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), _target, patchSize))
            {
                throw NewWin32Exception("刷新目标函数指令缓存失败");
            }
        }
        catch (Exception activationException)
        {
            if (patched)
            {
                try
                {
                    RestoreOriginalBytes(originalProtection);
                    Interlocked.Exchange(ref _active, 0);
                }
                catch (Exception restoreException)
                {
                    throw new AggregateException(
                        "Inline Hook 激活失败，且恢复原始代码也失败；目标函数可能仍处于修改状态",
                        activationException,
                        restoreException
                    );
                }
            }
            else
            {
                Interlocked.Exchange(ref _active, 0);
            }

            throw;
        }
    }

    public void Restore()
    {
        var previousState = Interlocked.CompareExchange(ref _active, 2, 1);
        if (previousState == 0)
        {
            return;
        }

        if (previousState != 1)
        {
            throw new InvalidOperationException("Inline Hook 正在由另一个线程恢复");
        }

        try
        {
            RestoreOriginalBytes();
            Volatile.Write(ref _active, 0);
        }
        catch
        {
            // 保守地保持“已激活”状态，允许调用方重试，并阻止清理失败被误报为成功。
            Volatile.Write(ref _active, 1);
            throw;
        }
    }

    private void RestoreOriginalBytes(uint? finalProtection = null)
    {
        var patchSize = (nuint)_patchSize;
        if (
            !NativeMethods.VirtualProtect(_target, patchSize, NativeMethods.PageExecuteReadWrite, out var oldProtection)
        )
        {
            throw NewWin32Exception("恢复 Inline Hook 时无法修改目标函数页保护");
        }

        fixed (byte* source = _originalBytes)
        {
            Buffer.MemoryCopy(source, (void*)_target, patchSize, patchSize);
        }

        var protectionRestored = NativeMethods.VirtualProtect(
            _target,
            patchSize,
            finalProtection
                ?? (Volatile.Read(ref _originalProtectionKnown) != 0 ? _originalProtection : oldProtection),
            out _
        );
        var protectionError = protectionRestored ? 0 : Marshal.GetLastWin32Error();
        var cacheFlushed = NativeMethods.FlushInstructionCache(
            NativeMethods.GetCurrentProcess(),
            _target,
            patchSize
        );
        var cacheError = cacheFlushed ? 0 : Marshal.GetLastWin32Error();

        if (!protectionRestored)
        {
            throw NewWin32Exception("恢复 Inline Hook 后无法还原目标函数页保护", protectionError);
        }

        if (!cacheFlushed)
        {
            throw NewWin32Exception("恢复 Inline Hook 后刷新指令缓存失败", cacheError);
        }
    }

    private static Win32Exception NewWin32Exception(string operation, int? error = null)
    {
        var nativeError = error ?? Marshal.GetLastWin32Error();
        return new Win32Exception(nativeError, $"{operation}（Win32 {nativeError}）");
    }

    private static void WriteAbsoluteJump(byte* destination, nint target)
    {
        *(ushort*)destination = 0x25FF;
        *(uint*)(destination + 2) = 0;
        *(nint*)(destination + 6) = target;
    }
}
