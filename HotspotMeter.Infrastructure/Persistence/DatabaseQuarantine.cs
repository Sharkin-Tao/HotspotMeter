using System.Globalization;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>隔离失败时抛出。原始文件可能部分移动，消息中会列出未能还原的文件。</summary>
public sealed class DatabaseQuarantineException : Exception
{
    public DatabaseQuarantineException(string message, Exception innerException, IReadOnlyList<string> notRestoredFiles)
        : base(message, innerException)
    {
        NotRestoredFiles = notRestoredFiles;
    }

    /// <summary>尝试还原但失败的文件（原路径）。为空表示原始文件已全部还原。</summary>
    public IReadOnlyList<string> NotRestoredFiles { get; }
}

/// <summary>隔离结果。</summary>
/// <param name="Directory">隔离目录；没有文件需要隔离时为 null。</param>
/// <param name="MovedFiles">被移动文件的原始路径。</param>
public sealed record DatabaseQuarantineResult(string? Directory, IReadOnlyList<string> MovedFiles);

/// <summary>
/// 把损坏的数据库及其附属文件整体移入隔离目录。
///
/// 为什么必须整体移动：SQLite 在 WAL 模式下，主库文件之外还有 <c>-wal</c>、<c>-shm</c>，
/// 回滚日志模式下有 <c>-journal</c>。只移动主文件会留下"孤儿"日志，
/// 下次创建同名库时可能被误当成新库的日志，造成更隐蔽的损坏。
///
/// 这里只做移动，绝不删除：损坏文件是事后分析（以及用户自行抢救）的唯一证据。
/// 移动失败时会尽量把已移动的文件还原，然后抛异常；调用方必须停止启动，
/// 而不是在新位置创建一个可能覆盖原始数据的新库。
/// </summary>
public sealed class DatabaseQuarantine
{
    private static readonly string[] SidecarSuffixes = { string.Empty, "-wal", "-shm", "-journal" };

    /// <summary>
    /// 隔离数据库文件。
    /// </summary>
    /// <param name="databasePath">主数据库文件路径。</param>
    /// <param name="corruptionRoot">隔离目录的父目录。</param>
    /// <param name="nowUtc">用于生成隔离目录名的时间。</param>
    public DatabaseQuarantineResult Quarantine(string databasePath, string corruptionRoot, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        if (string.IsNullOrWhiteSpace(corruptionRoot))
        {
            throw new ArgumentException("隔离目录不能为空。", nameof(corruptionRoot));
        }

        var existing = SidecarSuffixes
            .Select(suffix => (Source: databasePath + suffix, FileName: Path.GetFileName(databasePath) + suffix))
            .Where(item => File.Exists(item.Source))
            .ToList();

        if (existing.Count == 0)
        {
            return new DatabaseQuarantineResult(null, Array.Empty<string>());
        }

        Directory.CreateDirectory(corruptionRoot);
        var targetDirectory = CreateUniqueDirectory(corruptionRoot, nowUtc);

        var moved = new List<(string Source, string Target)>();
        try
        {
            foreach (var item in existing)
            {
                var target = Path.Combine(targetDirectory, item.FileName);
                File.Move(item.Source, target);
                moved.Add((item.Source, target));
            }

            return new DatabaseQuarantineResult(targetDirectory, moved.Select(item => item.Source).ToList());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var notRestored = new List<string>();
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(item.Target, item.Source);
                }
                catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                {
                    notRestored.Add(item.Source);
                }
            }

            // 失败时不要留下容易误导人的空隔离目录。
            TryDeleteEmptyDirectory(targetDirectory);

            throw new DatabaseQuarantineException(
                $"隔离损坏数据库失败：{exception.Message}。已停止启动，未创建新数据库。",
                exception,
                notRestored);
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string CreateUniqueDirectory(string corruptionRoot, DateTimeOffset nowUtc)
    {
        var stamp = nowUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(corruptionRoot, "corrupt-" + stamp);
        var counter = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(corruptionRoot, $"corrupt-{stamp}-{counter.ToString(CultureInfo.InvariantCulture)}");
            counter++;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }
}