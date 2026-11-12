namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 数据库与隔离目录的唯一定义处。
/// 所有 SQLite 相关路径必须从这里取，禁止各模块自行拼接，避免出现
/// "监控写到 A、统计读到 B" 这类难以排查的问题。
/// </summary>
public sealed class DatabasePaths
{
    /// <summary>用户数据目录下的应用文件夹名。</summary>
    public const string ApplicationFolderName = "HotspotMeter";

    /// <summary>数据库文件名。</summary>
    public const string DatabaseFileName = "hotspotmeter.db";

    /// <summary>损坏数据库隔离目录名（位于数据库目录下）。</summary>
    public const string CorruptionFolderName = "corrupt";

    /// <summary>
    /// 创建路径管理器。
    /// </summary>
    /// <param name="rootDirectory">
    /// 数据库目录的绝对路径。测试会传入临时目录；生产代码应使用 <see cref="CreateDefault"/>。
    /// </param>
    public DatabasePaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("数据库目录不能为空。", nameof(rootDirectory));
        }

        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("数据库目录必须是绝对路径。", nameof(rootDirectory));
        }

        var fullRoot = Path.GetFullPath(rootDirectory);

        // 需求明确要求数据库不得放在程序安装目录：安装目录通常是只读的，
        // 且卸载/升级时容易被整体删除，放在那里的数据没有任何保障。
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (IsSameOrUnder(fullRoot, applicationDirectory))
        {
            throw new ArgumentException(
                $"数据库目录不能位于程序目录内：{fullRoot}",
                nameof(rootDirectory));
        }

        RootDirectory = fullRoot;
    }

    /// <summary>数据库所在目录。</summary>
    public string RootDirectory { get; }

    /// <summary>主数据库文件。</summary>
    public string DatabaseFile => Path.Combine(RootDirectory, DatabaseFileName);

    /// <summary>损坏数据库隔离目录的父目录。</summary>
    public string CorruptionDirectory => Path.Combine(RootDirectory, CorruptionFolderName);

    /// <summary>
    /// 生产环境默认路径：%LOCALAPPDATA%\HotspotMeter\Database。
    /// 使用 LocalApplicationData 而不是 Roaming，因为流量数据与具体电脑绑定，
    /// 且 WAL 文件不适合漫游同步。
    /// </summary>
    public static DatabasePaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法获取 LocalApplicationData 目录。");
        }

        return new DatabasePaths(Path.Combine(localAppData, ApplicationFolderName, "Database"));
    }

    /// <summary>创建数据库目录与隔离目录（幂等）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(CorruptionDirectory);
    }

    private static bool IsSameOrUnder(string candidate, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}