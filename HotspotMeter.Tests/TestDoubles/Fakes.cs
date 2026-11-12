using HotspotMeter.Core.Abstractions;
using HotspotMeter.Core.Models;

namespace HotspotMeter.Tests.TestDoubles;

internal sealed class FakeMonotonicClock : IMonotonicClock
{
    public long ElapsedMilliseconds { get; set; }

    public void Advance(long milliseconds) => ElapsedMilliseconds += milliseconds;
}

internal sealed class FakeWallClock : IWallClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
}

internal sealed class FakeUptimeProvider : IUptimeProvider
{
    public long TickCount64 { get; set; }
}

internal sealed class FakeHotspotDetector : IHotspotDetector
{
    public HotspotConnection? Connection { get; set; }

    public Exception? Failure { get; set; }

    public Task<HotspotConnection?> GetConnectionAsync(Guid interfaceGuid, CancellationToken cancellationToken)
        => Failure is null
            ? Task.FromResult(Connection)
            : Task.FromException<HotspotConnection?>(Failure);

    public Task<IReadOnlyList<HotspotCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<HotspotCandidate>>(Array.Empty<HotspotCandidate>());
}

internal sealed class FakeTrafficCounter : ITrafficCounter
{
    public InterfaceReading? Reading { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>每次读取时动态产生读数；为 null 时退回 <see cref="Reading"/>。</summary>
    public Func<InterfaceReading?>? Provider { get; set; }

    public Task<InterfaceReading?> TryReadAsync(Guid interfaceGuid, CancellationToken cancellationToken)
        => Failure is null
            ? Task.FromResult(Provider is null ? Reading : Provider())
            : Task.FromException<InterfaceReading?>(Failure);
}

internal sealed class FakeContinuityProvider : IConnectionContinuityProvider
{
    public ContinuityAssessment Assessment { get; set; } = ContinuityAssessment.Unverified("测试环境下没有证据。");

    public int CallCount { get; private set; }

    public Task<ContinuityAssessment> AssessAsync(RecoveryWindow window, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Assessment);
    }
}