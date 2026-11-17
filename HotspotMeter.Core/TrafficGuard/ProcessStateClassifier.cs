namespace HotspotMeter.Core.TrafficGuard;

/// <summary>
/// 前台/后台/空闲判定。
///
/// 关键取舍：
/// 1. 有前台窗口的进程一定是 Foreground；
/// 2. 没有窗口不能直接判 Idle——托盘程序、更新服务、系统服务也可能在传流量；
///    因此只要本周期有可信增量，就判 Background；
/// 3. 有可见窗口但当前没有流量，仍视为 Background（用户可能刚切回来），
///    只有既不在前台、没有可见窗口、也没有网络增量时才判 Idle。
/// </summary>
public static class ProcessStateClassifier
{
    public static GuardActivityState Classify(
        bool isForeground,
        bool hasVisibleWindow,
        bool hasNetworkActivity)
    {
        if (isForeground)
        {
            return GuardActivityState.Foreground;
        }

        if (hasNetworkActivity || hasVisibleWindow)
        {
            return GuardActivityState.Background;
        }

        return GuardActivityState.Idle;
    }
}