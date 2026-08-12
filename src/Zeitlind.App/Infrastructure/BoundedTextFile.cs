using System.Text;

namespace Zeitlind.App.Infrastructure;

internal static class BoundedTextFile
{
    internal const int MaximumLength = 4 * 1024 * 1024;

    public static string ReadAllText(string path, string displayName)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumLength)
        {
            throw new InvalidDataException($"{displayName} 超过 4 MiB，拒绝读取");
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false
        );
        return reader.ReadToEnd();
    }
}
