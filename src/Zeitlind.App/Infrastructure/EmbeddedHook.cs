using System.ComponentModel;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using Zeitlind.App.Games;

namespace Zeitlind.App.Infrastructure;

internal static class EmbeddedHook
{
    private const string StagingDirectoryPrefix = "Zeitlind-hook-";
    private const string AdministratorOnlyDirectorySddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    public static ExtractedHook? TryExtract(IGameModule module)
    {
        var resourceName = module.Descriptor.HookResourceName;
        var assembly = Assembly.GetExecutingAssembly();
        using var source = assembly.GetManifestResourceStream(resourceName);
        if (source is null)
        {
            return null;
        }

        var data = ReadAllBytes(source);
        var digest = Convert.ToHexString(SHA256.HashData(data));
        var directory = CreateProtectedStagingDirectory();
        nint directoryHandle = 0;
        FileStream? fileLock = null;
        var destination = Path.Combine(directory, Path.GetFileName(resourceName));

        try
        {
            directoryHandle = OpenAndLockDirectory(directory);
            using (
                var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough
                )
            )
            {
                output.Write(data);
                output.Flush(flushToDisk: true);
            }

            fileLock = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stagedDigest = Convert.ToHexString(SHA256.HashData(fileLock));
            fileLock.Position = 0;
            if (!string.Equals(stagedDigest, digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("写入临时 Hook DLL 后校验失败");
            }

            ApplicationLog.WriteInfo($"Hook 临时文件：{destination}", writeToConsole: false);
            ApplicationLog.WriteDebug(
                $"Hook 资源：{data.Length} bytes；SHA-256 {digest}；已写入本次运行专用的受保护目录",
                writeToConsole: false
            );

            var result = new ExtractedHook(destination, directory, fileLock, directoryHandle);
            fileLock = null;
            directoryHandle = 0;
            return result;
        }
        catch
        {
            fileLock?.Dispose();
            if (directoryHandle != 0)
            {
                NativeMethods.CloseHandle(directoryHandle);
            }

            DeleteStagingDirectoryBestEffort(directory, "回滚 Hook 临时目录失败");
            throw;
        }
    }

    private static string CreateProtectedStagingDirectory()
    {
        if (
            !NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
                AdministratorOnlyDirectorySddl,
                NativeMethods.SddlRevision1,
                out var securityDescriptor,
                out _
            )
        )
        {
            throw SuspendedGameProcess.NewWin32Exception("无法创建 Hook 临时目录安全描述符");
        }

        try
        {
            var securityAttributes = new NativeMethods.SecurityAttributes
            {
                Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle = 0,
            };

            for (var attempt = 0; attempt < 10; attempt++)
            {
                var directory = Path.Combine(Path.GetTempPath(), StagingDirectoryPrefix + Guid.NewGuid().ToString("N"));
                if (NativeMethods.CreateDirectory(directory, ref securityAttributes))
                {
                    return directory;
                }

                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error is not NativeMethods.ErrorAlreadyExists and not NativeMethods.ErrorFileExists)
                {
                    throw new Win32Exception(error, "无法创建受保护的 Hook 临时目录");
                }
            }

            throw new IOException("无法为 Hook DLL 创建唯一的临时目录");
        }
        finally
        {
            _ = NativeMethods.LocalFree(securityDescriptor);
        }
    }

    private static nint OpenAndLockDirectory(string directory)
    {
        var handle = NativeMethods.CreateFile(
            directory,
            desiredAccess: 0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            securityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            templateFile: 0
        );
        if (handle == NativeMethods.InvalidHandleValue)
        {
            throw SuspendedGameProcess.NewWin32Exception("无法锁定 Hook 临时目录");
        }

        return handle;
    }

    private static void DeleteStagingDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }

        Directory.Delete(directory, recursive: false);
    }

    private static void DeleteStagingDirectoryBestEffort(string directory, string context)
    {
        try
        {
            DeleteStagingDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException)
        {
            ApplicationLog.WriteDebug($"{context}：{exception.Message}", writeToConsole: false);
        }
    }

    private static byte[] ReadAllBytes(Stream source)
    {
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    internal sealed class ExtractedHook : IDisposable
    {
        private readonly string _directory;
        private FileStream? _fileLock;
        private nint _directoryHandle;

        internal ExtractedHook(string path, string directory, FileStream fileLock, nint directoryHandle)
        {
            Path = path;
            _directory = directory;
            _fileLock = fileLock;
            _directoryHandle = directoryHandle;
        }

        internal string Path { get; }

        public void Dispose()
        {
            _fileLock?.Dispose();
            _fileLock = null;

            try
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception)
                when (exception is IOException or SecurityException or UnauthorizedAccessException)
            {
                ApplicationLog.WriteDebug($"删除 Hook 临时文件失败：{exception.Message}", writeToConsole: false);
            }

            if (_directoryHandle != 0)
            {
                NativeMethods.CloseHandle(_directoryHandle);
                _directoryHandle = 0;
            }

            try
            {
                Directory.Delete(_directory, recursive: false);
            }
            catch (Exception exception)
                when (exception is IOException or SecurityException or UnauthorizedAccessException)
            {
                ApplicationLog.WriteDebug($"删除 Hook 临时目录失败：{exception.Message}", writeToConsole: false);
            }
        }
    }
}
