using HotspotMeter.Core.Abstractions;
using HotspotMeter.Core.Models;
using HotspotMeter.Infrastructure.Interop;

namespace HotspotMeter.Infrastructure.Monitoring;

/// <summary>
/// WLAN API 实现的热点检测器。只回答"这块网卡现在连的是什么"，不判断是否目标热点
/// （匹配逻辑在 Core，便于单测）。
/// </summary>
public sealed class WlanHotspotDetector : IHotspotDetector, IDisposable
{
    private readonly WlanClient _client;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>打开 WLAN 句柄；失败时抛出，调用方应提示用户检查 WLAN 服务。</summary>
    public WlanHotspotDetector()
    {
        _client = WlanClient.Open();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HotspotCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EnumerateCandidates());
    }

    /// <inheritdoc />
    public Task<HotspotConnection?> GetConnectionAsync(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(QueryConnection(interfaceGuid));
    }

    private IReadOnlyList<HotspotCandidate> EnumerateCandidates()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var result = new List<HotspotCandidate>();

            foreach (var info in _client.EnumerateInterfaces())
            {
                var connected = info.IsState == (int)WlanInterfaceState.Connected;
                var hasAttributes = false;
                WlanConnectionAttributes attributes = default;
                if (connected)
                {
                    hasAttributes = _client.TryQueryConnection(info.InterfaceGuid, out attributes);
                }

                var ssid = hasAttributes ? WlanClient.DecodeSsid(attributes.Association.Dot11Ssid) : string.Empty;
                var bssid = hasAttributes ? WlanClient.FormatBssid(attributes.Association.Dot11Bssid) : string.Empty;

                result.Add(new HotspotCandidate(
                    info.InterfaceGuid,
                    info.InterfaceDescription ?? string.Empty,
                    ssid,
                    string.IsNullOrEmpty(bssid) ? null : bssid,
                    connected && ssid.Length > 0,
                    hasAttributes ? (int)attributes.Association.WlanSignalQuality : 0,
                    hasAttributes ? WlanClient.ToBitsPerSecond(attributes.Association.UlRxRate) : 0,
                    hasAttributes ? WlanClient.ToBitsPerSecond(attributes.Association.UlTxRate) : 0));
            }

            return result;
        }
    }

    private HotspotConnection? QueryConnection(Guid interfaceGuid)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            // ERROR_INVALID_STATE ⇒ 返回 false：这是"查询成功且未连接"，不是异常。
            if (!_client.TryQueryConnection(interfaceGuid, out var attributes))
            {
                return null;
            }

            if (attributes.IsState != (int)WlanInterfaceState.Connected)
            {
                return null;
            }

            var ssid = WlanClient.DecodeSsid(attributes.Association.Dot11Ssid);
            if (ssid.Length == 0)
            {
                return null;
            }

            var bssid = WlanClient.FormatBssid(attributes.Association.Dot11Bssid);
            return new HotspotConnection(
                interfaceGuid,
                ssid,
                string.IsNullOrEmpty(bssid) ? null : bssid,
                WlanClient.ToBitsPerSecond(attributes.Association.UlRxRate),
                WlanClient.ToBitsPerSecond(attributes.Association.UlTxRate));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _client.Dispose();
        }
    }
}