namespace Zeitlind.Hook.Zzz;

using Zeitlind.Hook.Common;

/// <summary>
/// 只读已经由游戏初始化的当前玩家 UID 对象链。
///
/// 不调用游戏 getter，不触发 IL2CPP 类型初始化，也不调用服务解析器。所有读取
/// 都通过 ReadProcessMemory 访问当前进程，使对象切换或销毁时的无效地址表现为
/// 普通读取失败，而不是让访问冲突逃入游戏。
/// </summary>
internal sealed unsafe class CurrentUidReader
{
    private readonly nint _process = NativeMethods.GetCurrentProcess();
    private readonly nint _rootSlotAddress;
    private readonly CurrentUidObjectLayout _objectLayout;

    public CurrentUidReader(CurrentUidLocation location)
    {
        _rootSlotAddress = location.RootSlotAddress;
        _objectLayout = location.ObjectLayout;
    }

    public bool TryRead(out uint uid)
    {
        uid = 0;

        if (
            !TryReadPointer(_rootSlotAddress, out var metadataUsageCell)
            || metadataUsageCell == 0
            || !TryReadPointer(metadataUsageCell, out var firstClass)
            || firstClass == 0
            || !IsClassInitialized(firstClass)
            || !TryReadPointer(firstClass + _objectLayout.FirstClassLinkOffset, out var firstLink)
            || firstLink == 0
            || !TryReadPointer(firstLink + _objectLayout.SecondClassOffset, out var secondClass)
            || secondClass == 0
            || !IsClassInitialized(secondClass)
            || !TryReadPointer(secondClass + _objectLayout.StaticInstanceSlotOffset, out var staticInstanceSlot)
            || staticInstanceSlot == 0
            || !TryReadPointer(staticInstanceSlot, out var owner)
            || owner == 0
            || !TryReadPointer(owner + _objectLayout.CachedServiceOffset, out var service)
            || service == 0
            || !TryReadUInt32(service + _objectLayout.UidOffset, out uid)
            || uid == 0
        )
        {
            uid = 0;
            return false;
        }

        return true;
    }

    private bool IsClassInitialized(nint classAddress)
    {
        return TryReadByte(classAddress + _objectLayout.ClassInitializedFlagOffset, out var initialized)
            && (initialized & 1) != 0;
    }

    private bool TryReadPointer(nint address, out nint value)
    {
        nint readValue = 0;
        var succeeded = NativeMethods.ReadProcessMemory(
            _process,
            address,
            &readValue,
            (nuint)sizeof(nint),
            out var bytesRead
        );
        value = readValue;
        return succeeded && bytesRead == (nuint)sizeof(nint);
    }

    private bool TryReadUInt32(nint address, out uint value)
    {
        uint readValue = 0;
        var succeeded = NativeMethods.ReadProcessMemory(_process, address, &readValue, sizeof(uint), out var bytesRead);
        value = readValue;
        return succeeded && bytesRead == sizeof(uint);
    }

    private bool TryReadByte(nint address, out byte value)
    {
        byte readValue = 0;
        var succeeded = NativeMethods.ReadProcessMemory(_process, address, &readValue, sizeof(byte), out var bytesRead);
        value = readValue;
        return succeeded && bytesRead == sizeof(byte);
    }
}
