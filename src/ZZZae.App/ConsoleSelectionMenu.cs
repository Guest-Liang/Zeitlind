namespace ZZZae.App;

internal static class ConsoleSelectionMenu
{
    public static int Read(
        IReadOnlyList<string> options,
        int selected,
        CancellationToken cancellationToken,
        int? escapeSelection = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selected);
        if (options.Count == 0 || selected >= options.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selected));
        }
        if (escapeSelection is < 0 || escapeSelection >= options.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(escapeSelection));
        }

        var menuTop = Console.CursorTop;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            menuTop = Math.Min(menuTop, Math.Max(0, Console.BufferHeight - options.Count));
            Render(options, selected, menuTop);

            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.UpArrow:
                    selected = selected == 0 ? options.Count - 1 : selected - 1;
                    break;

                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % options.Count;
                    break;

                case ConsoleKey.Enter:
                    MoveBelowMenu(menuTop, options.Count);
                    return selected;

                case ConsoleKey.Escape when escapeSelection is { } cancel:
                    MoveBelowMenu(menuTop, options.Count);
                    return cancel;
            }
        }
    }

    private static void Render(IReadOnlyList<string> options, int selected, int menuTop)
    {
        var clearWidth = Math.Max(1, Console.BufferWidth - 1);
        using var output = new StreamWriter(
            Console.OpenStandardOutput(),
            Console.OutputEncoding,
            bufferSize: 256,
            leaveOpen: true
        )
        {
            AutoFlush = true,
        };

        for (var index = 0; index < options.Count; index++)
        {
            Console.SetCursorPosition(0, menuTop + index);
            output.Write(new string(' ', clearWidth));
            Console.SetCursorPosition(0, menuTop + index);
            output.Write(index == selected ? $"> {options[index]}" : $"  {options[index]}");
        }
    }

    private static void MoveBelowMenu(int menuTop, int optionCount)
    {
        var targetTop = menuTop + optionCount;
        if (targetTop < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, targetTop);
            return;
        }

        Console.SetCursorPosition(0, Math.Max(0, Console.BufferHeight - 1));
        Console.WriteLine();
    }
}
