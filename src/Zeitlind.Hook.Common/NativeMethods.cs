using System.Runtime.InteropServices;

namespace Zeitlind.Hook.Common;

public static partial class NativeMethods
{
    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint ImageScnMemExecute = 0x2000_0000;
    public const uint ImageScnMemRead = 0x4000_0000;
    public const uint ImageScnMemWrite = 0x8000_0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct ModuleInformation
    {
        public nint BaseOfDll;
        public uint SizeOfImage;
        public nint EntryPoint;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleInformation(
        nint process,
        nint module,
        out ModuleInformation moduleInformation,
        uint size
    );

    [LibraryImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        void* buffer,
        nuint size,
        out nuint bytesRead
    );

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualAlloc", SetLastError = true)]
    public static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualProtect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(nint address, nuint size, uint newProtection, out uint oldProtection);

    [LibraryImport("kernel32.dll", EntryPoint = "FlushInstructionCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint process, nint baseAddress, nuint size);
}
