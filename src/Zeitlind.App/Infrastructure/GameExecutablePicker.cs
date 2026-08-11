namespace Zeitlind.App.Infrastructure;

internal static class GameExecutablePicker
{
    private const int FileBufferLength = 32_768;
    private const uint ExplorerStyle = 0x0008_0000;
    private const uint FileMustExist = 0x0000_1000;
    private const uint PathMustExist = 0x0000_0800;
    private const uint NoChangeDirectory = 0x0000_0008;
    private const uint HideReadOnly = 0x0000_0004;

    public static unsafe string? Pick(string gameName, string executableName, string? initialExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var fileBuffer = new char[FileBufferLength];
        if (!string.IsNullOrWhiteSpace(initialExecutablePath) && initialExecutablePath.Length < fileBuffer.Length)
        {
            initialExecutablePath.CopyTo(0, fileBuffer, 0, initialExecutablePath.Length);
        }

        var filter = "可执行文件 (*.exe)\0*.exe\0\0";
        var title = $"选择{gameName}游戏程序（{executableName}）";

        fixed (char* file = fileBuffer)
        fixed (char* filterPointer = filter)
        fixed (char* titlePointer = title)
        {
            var dialog = new NativeMethods.OpenFileName
            {
                Size = checked((uint)sizeof(NativeMethods.OpenFileName)),
                Owner = 0,
                Filter = filterPointer,
                FilterIndex = 1,
                File = file,
                MaxFile = checked((uint)fileBuffer.Length),
                Title = titlePointer,
                Flags = ExplorerStyle | FileMustExist | PathMustExist | NoChangeDirectory | HideReadOnly,
            };

            if (NativeMethods.GetOpenFileName(&dialog))
            {
                return new string(file);
            }
        }

        var extendedError = NativeMethods.CommDlgExtendedError();
        if (extendedError == 0)
        {
            return null;
        }

        throw new IOException($"Windows 文件选择窗口返回错误 0x{extendedError:X8}");
    }
}
