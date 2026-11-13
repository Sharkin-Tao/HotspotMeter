using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 把 Core 的公开身份 <see cref="Guid"/> 解析为数据库内部 <c>ProfileId</c>。
///
/// 设计要点：
/// 1. 公开身份存 <c>ProfileKey</c>（Guid 的 32 位 N 格式），整数 ProfileId 只在本层使用，
///    Core 永远不接触数据库自增 Id。
/// 2. 内存缓存只是加速，命中缓存前不改变“数据库是事实来源”的定位：缓存值来自一次真实读取或写入回读，
///    进程重启后缓存清空，重新从数据库解析。
/// 3. 首次创建用 <c>INSERT ... ON CONFLICT DO NOTHING</c> + 回读，配合唯一索引处理并发首次创建；
///    两个进程同时创建同一个 Profile 时只有一个插入成功，另一个回读到同一行。
/// </summary>
public sealed class ProfileResolver
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ConcurrentDictionary<Guid, long> _cache = new();

    public ProfileResolver(SqliteConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// 解析或创建 Profile，返回数据库内部 Id。
    /// </summary>
    /// <param name="coreProfileId">Core 的 <c>HotspotTarget.ProfileId</c>。</param>
    /// <param name="interfaceGuid">热点所在无线接口 GUID，用于展示与诊断。</param>
    /// <param name="ssid">SSID，仅用于展示；身份仍以 <paramref name="coreProfileId"/> 为准。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<long> GetOrCreateAsync(
        Guid coreProfileId,
        Guid interfaceGuid,
        string ssid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            throw new ArgumentException("SSID 不能为空。", nameof(ssid));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_cache.TryGetValue(coreProfileId, out var cached))
        {
            return Task.FromResult(cached);
        }

        var key = coreProfileId.ToString("N");
        using var connection = _factory.Create();

        var existing = FindProfileId(connection, key);
        if (existing is null)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO HotspotProfiles
                        (ProfileKey, DisplayName, InterfaceGuid, Ssid, CreatedAtUtc, LastSeenUtc)
                    VALUES ($key, $name, $guid, $ssid, $now, $now)
                    ON CONFLICT(ProfileKey) DO NOTHING;
                    """;
                var now = DateTimeOffset.UtcNow.ToString("O");
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$name", ssid);
                insert.Parameters.AddWithValue("$guid", interfaceGuid.ToString("D"));
                insert.Parameters.AddWithValue("$ssid", ssid);
                insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
            }

            existing = FindProfileId(connection, key)
                ?? throw new InvalidOperationException($"ProfileKey {key} 写入后无法回读，数据库可能被并发修改。");
            transaction.Commit();
        }

        _cache[coreProfileId] = existing.Value;
        Touch(existing.Value, interfaceGuid, ssid);
        return Task.FromResult(existing.Value);
    }

    /// <summary>只查询，不创建。用于诊断与测试。</summary>
    public Task<long?> FindAsync(Guid coreProfileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(coreProfileId, out var cached))
        {
            return Task.FromResult<long?>(cached);
        }

        using var connection = _factory.Create();
        return Task.FromResult(FindProfileId(connection, coreProfileId.ToString("N")));
    }

    private static long? FindProfileId(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId FROM HotspotProfiles WHERE ProfileKey = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private void Touch(long profileId, Guid interfaceGuid, string ssid)
    {
        try
        {
            using var connection = _factory.Create();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE HotspotProfiles
                SET LastSeenUtc = $now, InterfaceGuid = $guid, Ssid = $ssid
                WHERE ProfileId = $id;
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$guid", interfaceGuid.ToString("D"));
            command.Parameters.AddWithValue("$ssid", ssid);
            command.Parameters.AddWithValue("$id", profileId);
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // LastSeenUtc 只是展示信息，失败不得影响身份解析这一核心职责。
        }
    }
}