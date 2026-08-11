using System.Text;

namespace Zeitlind.App.Infrastructure;

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("无法确定导出文件目录");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, Utf8WithoutBom, cancellationToken);
            File.Move(temporaryPath, fullPath, false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // 成功写入目标文件比尽力清理中断操作留下的临时文件更重要。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }
        }
    }
}
