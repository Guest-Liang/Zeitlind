using System.Reflection;
using System.Security.Cryptography;
using Zeitlind.App.Games;

namespace Zeitlind.App.Infrastructure;

internal static class EmbeddedHook
{
    public static string? TryExtract(IGameModule module)
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
        var directory = Path.Combine(Path.GetTempPath(), "Zeitlind", module.Descriptor.Id, digest[..16]);
        var destination = Path.Combine(directory, Path.GetFileName(resourceName));

        Directory.CreateDirectory(directory);

        var reused = HasSameContent(destination, data);
        if (!reused)
        {
            File.WriteAllBytes(destination, data);
        }

        ApplicationLog.WriteInfo($"Hook 临时文件：{destination}", writeToConsole: false);
        ApplicationLog.WriteDebug(
            $"Hook 资源：{data.Length} bytes；SHA-256 {digest}；"
                + $"临时文件{(reused ? "已存在并复用" : "已重新写入")}",
            writeToConsole: false
        );
        return destination;
    }

    private static byte[] ReadAllBytes(Stream source)
    {
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static bool HasSameContent(string path, ReadOnlySpan<byte> expected)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var actual = File.ReadAllBytes(path);
            return actual.AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
