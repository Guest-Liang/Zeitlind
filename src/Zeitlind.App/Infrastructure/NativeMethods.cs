using System.Runtime.InteropServices;

namespace Zeitlind.App.Infrastructure;

internal static partial class NativeMethods
{
    internal const uint CreateSuspended = 0x0000_0004;
    internal const uint MemCommit = 0x0000_1000;
    internal const uint MemReserve = 0x0000_2000;
    internal const uint MemRelease = 0x0000_8000;
    internal const uint PageReadWrite = 0x04;
    internal const uint Infinite = 0xFFFF_FFFF;
    internal const uint WaitObject0 = 0x0000_0000;
    internal const uint WaitTimeout = 0x0000_0102;
    internal const uint WaitFailed = 0xFFFF_FFFF;
    internal const uint ThreadStillActive = 259;
    internal const uint Th32CsSnapModule = 0x0000_0008;
    internal const uint Th32CsSnapModule32 = 0x0000_0010;
    internal const uint DontResolveDllReferences = 0x0000_0001;
    internal const uint GetModuleHandleExUnchangedRefCount = 0x0000_0002;
    internal const uint GetModuleHandleExFromAddress = 0x0000_0004;

    internal static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal uint Size;
        private nint _reserved;
        private nint _desktop;
        private nint _title;
        private uint _x;
        private uint _y;
        private uint _xSize;
        private uint _ySize;
        private uint _xCountChars;
        private uint _yCountChars;
        private uint _fillAttribute;
        private uint _flags;
        private ushort _showWindow;
        private ushort _reserved2Length;
        private nint _reserved2;
        private nint _standardInput;
        private nint _standardOutput;
        private nint _standardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct ModuleEntry
    {
        internal uint Size;
        private uint _moduleId;
        private uint _processId;
        private uint _globalUsageCount;
        private uint _processUsageCount;
        internal nint BaseAddress;
        private uint _baseSize;
        private nint _module;
        internal fixed char ModuleName[256];
        internal fixed char ExecutablePath[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OpenFileName
    {
        internal uint Size;
        internal nint Owner;
        internal nint Instance;
        internal char* Filter;
        internal char* CustomFilter;
        internal uint MaxCustomFilter;
        internal uint FilterIndex;
        internal char* File;
        internal uint MaxFile;
        internal char* FileTitle;
        internal uint MaxFileTitle;
        internal char* InitialDirectory;
        internal char* Title;
        internal uint Flags;
        internal ushort FileOffset;
        internal ushort FileExtension;
        internal char* DefaultExtension;
        internal nint CustomData;
        internal nint Hook;
        internal char* TemplateName;
        internal nint Reserved;
        internal uint ReservedValue;
        internal uint ExtendedFlags;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcess(
        string applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation
    );

    [LibraryImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    internal static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualAllocEx", SetLastError = true)]
    internal static partial nint VirtualAllocEx(
        nint process,
        nint address,
        nuint size,
        uint allocationType,
        uint protection
    );

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualFreeEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteProcessMemory(
        nint process,
        nint baseAddress,
        byte* buffer,
        nuint size,
        out nuint written
    );

    [LibraryImport("kernel32.dll", EntryPoint = "CreateRemoteThread", SetLastError = true)]
    internal static partial nint CreateRemoteThread(
        nint process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId
    );

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeThread", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeThread(nint thread, out uint exitCode);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    internal static partial nint GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetModuleHandleEx(uint flags, nint moduleNameOrAddress, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    internal static unsafe partial uint GetModuleFileName(nint module, char* fileName, uint size);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "LoadLibraryExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    internal static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeLibrary(nint module);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetProcAddress",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial nint GetProcAddress(nint module, string procedureName);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Module32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Module32First(nint snapshot, ModuleEntry* moduleEntry);

    [LibraryImport("kernel32.dll", EntryPoint = "Module32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Module32Next(nint snapshot, ModuleEntry* moduleEntry);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetOpenFileName(OpenFileName* openFileName);

    [LibraryImport("comdlg32.dll", EntryPoint = "CommDlgExtendedError")]
    internal static partial uint CommDlgExtendedError();
}
