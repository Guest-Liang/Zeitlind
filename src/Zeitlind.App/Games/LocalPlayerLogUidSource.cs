using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zeitlind.App.Games;

/// <summary>
/// Monitors local game logs from a baseline captured before the game process is started.
/// Only values written after that baseline are accepted, so a UID left by an older login
/// cannot be associated with the achievement snapshot from this run.
/// </summary>
internal sealed class LocalPlayerLogUidSource
{
    private const int MaximumReadBytes = 16 * 1024 * 1024;
    private const int CheckpointBytes = 64;
    private const int TailOverlapBytes = 512;

    private static readonly Regex LogUidPattern = new(
        @"\b(?:uid|user_id|role_id)\b\s*""?\s*[:=]\s*""?([1-9][0-9]{7,9})(?=&|,|\s|""|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex ValueFileUidPattern = new(
        @"^\s*\uFEFF?([1-9][0-9]{7,9})\s*$",
        RegexOptions.CultureInvariant
    );

    private readonly FileCursor[] _files;
    private ulong? _lastAcceptedUid;
    private DateTimeOffset? _lastAcceptedWriteTimeUtc;
    private string? _lastDecision;

    public LocalPlayerLogUidSource(
        IReadOnlyList<(string Path, bool IsValueFile)> files,
        ulong minimumUid,
        ulong maximumUid
    )
    {
        ArgumentNullException.ThrowIfNull(files);
        if (minimumUid == 0 || maximumUid < minimumUid)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumUid), "UID 范围无效");
        }

        _files = files
            .DistinctBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => new FileCursor(file.Path, file.IsValueFile, minimumUid, maximumUid))
            .ToArray();
    }

    public bool TryReadNewUid(out ulong uid, out string detail)
    {
        var observations = new List<UidObservation>();
        foreach (var file in _files)
        {
            if (file.TryReadNewUid(out var observation))
            {
                observations.Add(observation);
            }
        }

        if (observations.Count == 0)
        {
            uid = 0;
            detail = string.Empty;
            return false;
        }

        var newestWrite = observations.Max(static observation => observation.LastWriteTimeUtc);
        if (_lastAcceptedWriteTimeUtc is not null && newestWrite < _lastAcceptedWriteTimeUtc.Value)
        {
            uid = 0;
            detail = string.Empty;
            _lastDecision = "忽略了晚读取但文件修改时间早于已确认记录的 UID";
            return false;
        }

        var newest = observations.Where(observation => observation.LastWriteTimeUtc == newestWrite).ToArray();
        var distinctUids = newest.Select(static observation => observation.Uid).Distinct().ToArray();
        if (distinctUids.Length != 1)
        {
            uid = 0;
            detail = string.Empty;
            _lastDecision = $"最新修改的本地日志出现 {distinctUids.Length} 个相互冲突的 UID，已拒绝猜测";
            return false;
        }

        var selectedUid = distinctUids[0];
        if (
            _lastAcceptedWriteTimeUtc == newestWrite
            && _lastAcceptedUid is not null
            && _lastAcceptedUid.Value != selectedUid
        )
        {
            uid = 0;
            detail = string.Empty;
            _lastDecision = "与已确认记录修改时间相同的本地日志出现不同 UID，已拒绝猜测";
            return false;
        }

        _lastAcceptedUid = selectedUid;
        _lastAcceptedWriteTimeUtc = newestWrite;
        uid = selectedUid;
        var sources = newest
            .Where(observation => observation.Uid == selectedUid)
            .Select(static observation => Path.GetFileName(observation.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        detail = $"启动后更新的本地文件 {string.Join("/", sources)}";
        _lastDecision = $"已从{detail}读取到当前 UID";
        return true;
    }

    public string FormatDiagnostics()
    {
        var changed = _files.Count(static file => file.HasChangedSinceBaseline);
        var existing = _files.Count(static file => file.ExistsNow);
        var state = $"本地 UID 文件：监视 {_files.Length} 个路径，当前存在 {existing} 个，启动后变化 {changed} 个";
        return _lastDecision is null ? state : $"{state}；{_lastDecision}";
    }

    private sealed class FileCursor
    {
        private readonly string _path;
        private readonly bool _isValueFile;
        private readonly ulong _minimumUid;
        private readonly ulong _maximumUid;

        private bool _existedAtBaseline;
        private long _creationTimeUtcTicks;
        private long _lastWriteTimeUtcTicks;
        private long _offset;
        private byte[] _checkpoint = [];
        private byte[] _pendingTail = [];

        public FileCursor(string path, bool isValueFile, ulong minimumUid, ulong maximumUid)
        {
            _path = path;
            _isValueFile = isValueFile;
            _minimumUid = minimumUid;
            _maximumUid = maximumUid;

            if (TryGetStamp(out var baseline))
            {
                _existedAtBaseline = true;
                _creationTimeUtcTicks = baseline.CreationTimeUtcTicks;
                _lastWriteTimeUtcTicks = baseline.LastWriteTimeUtcTicks;
                _offset = baseline.Length;
                _checkpoint = ReadCheckpointBestEffort(_offset);
                ExistsNow = true;
            }
        }

        public bool HasChangedSinceBaseline { get; private set; }

        public bool ExistsNow { get; private set; }

        public bool TryReadNewUid(out UidObservation observation)
        {
            observation = default;
            try
            {
                if (!TryGetStamp(out var stamp))
                {
                    ExistsNow = false;
                    return false;
                }

                ExistsNow = true;
                var previousOffset = _offset;
                var createdAfterBaseline = !_existedAtBaseline;
                var replaced =
                    _existedAtBaseline
                    && _creationTimeUtcTicks != 0
                    && stamp.CreationTimeUtcTicks != _creationTimeUtcTicks;
                var truncated = stamp.Length < _offset;
                var rewrittenAtSameLength =
                    stamp.Length == _offset && stamp.LastWriteTimeUtcTicks != _lastWriteTimeUtcTicks;
                var checkpointChanged = _offset > 0 && stamp.Length >= _offset && !CheckpointStillMatches(_offset);

                if (createdAfterBaseline || replaced || truncated || rewrittenAtSameLength || checkpointChanged)
                {
                    _offset = 0;
                    _checkpoint = [];
                    _pendingTail = [];
                }

                _existedAtBaseline = true;
                _creationTimeUtcTicks = stamp.CreationTimeUtcTicks;
                var changed =
                    createdAfterBaseline
                    || replaced
                    || truncated
                    || rewrittenAtSameLength
                    || checkpointChanged
                    || stamp.Length > _offset;
                _lastWriteTimeUtcTicks = stamp.LastWriteTimeUtcTicks;
                if (!changed)
                {
                    return false;
                }

                HasChangedSinceBaseline = true;
                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                var available = stream.Length - _offset;
                if (available <= 0)
                {
                    return false;
                }

                var bytesToRead = (int)Math.Min(available, MaximumReadBytes);
                var readStart = available > MaximumReadBytes ? stream.Length - MaximumReadBytes : _offset;
                var newBytes = GC.AllocateUninitializedArray<byte>(bytesToRead);
                stream.Seek(readStart, SeekOrigin.Begin);
                stream.ReadExactly(newBytes);
                _offset = stream.Length;
                _checkpoint = ReadCheckpoint(stream, _offset);

                byte[] data;
                var readWasContiguousAppend = readStart == previousOffset;
                if (_isValueFile || _pendingTail.Length == 0 || !readWasContiguousAppend)
                {
                    data = newBytes;
                }
                else
                {
                    data = [.. _pendingTail, .. newBytes];
                }

                _pendingTail = _isValueFile ? [] : data[^Math.Min(data.Length, TailOverlapBytes)..];
                var text = Encoding.UTF8.GetString(data);
                if (!TryParseText(text, out var uid))
                {
                    return false;
                }

                observation = new UidObservation(
                    uid,
                    _path,
                    new DateTimeOffset(stamp.LastWriteTimeUtcTicks, TimeSpan.Zero)
                );
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The game may rotate or briefly lock a log between the metadata check and the read.
                return false;
            }
        }

        private bool TryParseText(string text, out ulong uid)
        {
            uid = 0;
            var found = false;
            var matches = (_isValueFile ? ValueFileUidPattern : LogUidPattern).Matches(text);
            foreach (var match in matches.Cast<Match>())
            {
                if (
                    ulong.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var value
                    )
                    && value >= _minimumUid
                    && value <= _maximumUid
                )
                {
                    uid = value;
                    found = true;
                }
            }

            return found;
        }

        private bool TryGetStamp(out FileStamp stamp)
        {
            try
            {
                var file = new FileInfo(_path);
                if (!file.Exists)
                {
                    stamp = default;
                    return false;
                }

                stamp = new FileStamp(file.Length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                stamp = default;
                return false;
            }
        }

        private bool CheckpointStillMatches(long endOffset)
        {
            if (_checkpoint.Length == 0)
            {
                return true;
            }

            try
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                return ReadCheckpoint(stream, endOffset).AsSpan().SequenceEqual(_checkpoint);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private byte[] ReadCheckpointBestEffort(long endOffset)
        {
            try
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                return ReadCheckpoint(stream, endOffset);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static byte[] ReadCheckpoint(FileStream stream, long endOffset)
        {
            var length = (int)Math.Min(endOffset, CheckpointBytes);
            if (length == 0)
            {
                return [];
            }

            var checkpoint = GC.AllocateUninitializedArray<byte>(length);
            stream.Seek(endOffset - length, SeekOrigin.Begin);
            stream.ReadExactly(checkpoint);
            return checkpoint;
        }
    }

    private readonly record struct FileStamp(long Length, long CreationTimeUtcTicks, long LastWriteTimeUtcTicks);

    private readonly record struct UidObservation(ulong Uid, string Path, DateTimeOffset LastWriteTimeUtc);
}

internal static class LocalLogUidFiles
{
    private static readonly string HsrDirectory = Path.Combine(LocalLowRoot, "miHoYo", "崩坏：星穹铁道");
    private static readonly string GenshinDirectory = Path.Combine(LocalLowRoot, "miHoYo", "原神");
    private static readonly string ZzzDirectory = Path.Combine(LocalLowRoot, "miHoYo", "绝区零");

    public static IReadOnlyList<(string Path, bool IsValueFile)> Hsr { get; } =
    [(Path.Combine(HsrDirectory, "Player.log"), false), (Path.Combine(HsrDirectory, "output_log.txt"), false)];

    public static IReadOnlyList<(string Path, bool IsValueFile)> Genshin { get; } =
    [(Path.Combine(GenshinDirectory, "output_log.txt"), false), (Path.Combine(GenshinDirectory, "UidInfo.txt"), true)];

    public static IReadOnlyList<(string Path, bool IsValueFile)> Zzz { get; } =
    [(Path.Combine(ZzzDirectory, "Player.log"), false), (Path.Combine(ZzzDirectory, "output_log.txt"), false)];

    private static string LocalLowRoot
    {
        get
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appData = Path.GetDirectoryName(roaming);
            return string.IsNullOrWhiteSpace(appData) ? string.Empty : Path.Combine(appData, "LocalLow");
        }
    }
}
