using Microsoft.Extensions.Logging;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>初始化结果，供 3B 及后续模块记录启动上下文。</summary>
public sealed record DatabaseInitializationResult(
    DatabasePaths Paths,
    int SchemaVersion,
    bool WasQuarantined,
    string? QuarantineDirectory,
    DatabaseHealthResult? HealthBefore);

/// <summary>
/// 数据库启动编排：建目录 → 健康检查 → 必要时隔离 → 迁移 → 返回结果。
///
/// 顺序不可交换：必须先确认旧文件不可用并完成隔离，才允许创建新库；
/// 否则新库会覆盖原文件名，损坏数据将无法追查。
/// </summary>
public sealed class DatabaseBootstrapper
{
    private readonly DatabasePaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseHealth _health;
    private readonly DatabaseQuarantine _quarantine;
    private readonly SchemaMigrator _migrator;
    private readonly ILogger<DatabaseBootstrapper>? _logger;

    public DatabaseBootstrapper(
        DatabasePaths paths,
        SqliteConnectionFactory? factory = null,
        DatabaseHealth? health = null,
        DatabaseQuarantine? quarantine = null,
        SchemaMigrator? migrator = null,
        ILogger<DatabaseBootstrapper>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _factory = factory ?? new SqliteConnectionFactory(paths.DatabaseFile);
        _health = health ?? new DatabaseHealth();
        _quarantine = quarantine ?? new DatabaseQuarantine();
        _migrator = migrator ?? new SchemaMigrator();
        _logger = logger;
    }

    /// <summary>执行初始化。</summary>
    public DatabaseInitializationResult Initialize()
    {
        _paths.EnsureDirectories();

        DatabaseHealthResult? healthBefore = null;
        var quarantined = false;
        string? quarantineDirectory = null;

        if (File.Exists(_paths.DatabaseFile))
        {
            healthBefore = _health.Check(_paths.DatabaseFile);
            if (!healthBefore.IsHealthy)
            {
                _logger?.LogError(
                    "数据库健康检查失败，准备隔离：{Detail}",
                    healthBefore.Detail);

                var result = _quarantine.Quarantine(_paths.DatabaseFile, _paths.CorruptionDirectory, DateTimeOffset.UtcNow);
                quarantined = result.Directory is not null;
                quarantineDirectory = result.Directory;
                _logger?.LogWarning(
                    "已隔离 {Count} 个文件到 {Directory}",
                    result.MovedFiles.Count,
                    result.Directory);
            }
        }

        using var connection = _factory.Create();
        var version = _migrator.Migrate(connection);

        return new DatabaseInitializationResult(
            _paths,
            version,
            quarantined,
            quarantineDirectory,
            healthBefore);
    }
}