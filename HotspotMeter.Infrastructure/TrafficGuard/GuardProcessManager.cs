using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Infrastructure.TrafficGuard;

/// <summary>
/// 手动结束进程的实现。
///
/// 结束前必须重新枚举进程并核对 PID + 启动时间 + 路径/PFN：PID 会被系统复用，
/// 只凭用户确认时的 PID 直接 Kill 可能误杀新进程。安全策略在 Core 中，
/// 这里只负责读取真实系统状态和执行 Kill。
/// </summary>
public sealed class GuardProcessManager : IGuardProcessService
{
    private readonly IProcessInspector _inspector;
    private readonly string _windowsDirectory;
    private readonly TimeSpan _startTimeTolerance;

    public GuardProcessManager(
        IProcessInspector inspector,
        string? windowsDirectory = null,
        TimeSpan? startTimeTolerance = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _windowsDirectory = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _startTimeTolerance = startTimeTolerance ?? TimeSpan.FromSeconds(2);
    }

    public Task<IReadOnlyList<GuardProcessCandidate>> GetProcessesAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        return Task.Run<IReadOnlyList<GuardProcessCandidate>>(() =>
        {
            var items = new List<GuardProcessCandidate>();
            foreach (var observation in _inspector.Capture())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(appKey, observation))
                {
                    continue;
                }

                var sessionId = GetSessionId(observation.ProcessId);
                var safety = GuardProcessPolicy.Evaluate(
                    appKey,
                    observation.ExecutablePath,
                    sessionId,
                    _windowsDirectory);

                items.Add(new GuardProcessCandidate(
                    observation.ProcessId,
                    appKey,
                    observation.ProcessName,
                    observation.ExecutablePath,
                    observation.PackageFamilyName,
                    new DateTimeOffset(observation.StartTimeUtcTicks, TimeSpan.Zero),
                    sessionId,
                    safety));
            }

            return items.OrderBy(item => item.ProcessId).ToArray();
        }, cancellationToken);
    }

    public Task<GuardTerminationResult> TerminateAsync(
        GuardTerminationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => TerminateCore(request, cancellationToken), cancellationToken);
    }

    private GuardTerminationResult TerminateCore(
        GuardTerminationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        try
        {
            process = Process.GetProcessById(request.ProcessId);
        }
        catch (ArgumentException)
        {
            return new GuardTerminationResult(GuardProcessActionStatus.AlreadyExited);
        }
        catch (InvalidOperationException)
        {
            return new GuardTerminationResult(GuardProcessActionStatus.AlreadyExited);
        }
        catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
        {
            return new GuardTerminationResult(GuardProcessActionStatus.AccessDenied, exception.Message);
        }

        using (process)
        {
            if (process.HasExited)
            {
                return new GuardTerminationResult(GuardProcessActionStatus.AlreadyExited);
            }

            // 用一次新的系统枚举核对身份，避免只信任 UI 传回的路径/PFN。
            var observation = _inspector.Capture()
                .FirstOrDefault(item => item.ProcessId == request.ProcessId);
            if (observation is null)
            {
                return new GuardTerminationResult(GuardProcessActionStatus.AlreadyExited);
            }

            if (!Matches(request.AppKey, observation)
                || !StartTimeMatches(request.ExpectedStartedAtUtc, observation.StartTimeUtcTicks)
                || !ExpectedIdentityMatches(request, observation))
            {
                return new GuardTerminationResult(GuardProcessActionStatus.IdentityChanged);
            }

            var sessionId = GetSessionId(request.ProcessId);
            var safety = GuardProcessPolicy.Evaluate(
                request.AppKey,
                observation.ExecutablePath,
                sessionId,
                _windowsDirectory);
            if (!safety.CanTerminate)
            {
                return new GuardTerminationResult(
                    GuardProcessActionStatus.Blocked,
                    BlockMessage(safety.Reason),
                    safety.Reason);
            }

            try
            {
                process.Kill(entireProcessTree: false);
                if (!process.WaitForExit(3000))
                {
                    return new GuardTerminationResult(
                        GuardProcessActionStatus.Failed,
                        "进程已请求结束，但在 3 秒内没有退出。");
                }

                return new GuardTerminationResult(GuardProcessActionStatus.Success);
            }
            catch (InvalidOperationException)
            {
                return new GuardTerminationResult(GuardProcessActionStatus.AlreadyExited);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
            {
                return new GuardTerminationResult(
                    GuardProcessActionStatus.AccessDenied,
                    "权限不足，可能该进程由其他用户或管理员运行。");
            }
            catch (Win32Exception exception)
            {
                return new GuardTerminationResult(GuardProcessActionStatus.Failed, exception.Message);
            }
            catch (NotSupportedException exception)
            {
                return new GuardTerminationResult(GuardProcessActionStatus.AccessDenied, exception.Message);
            }
        }
    }

    private static bool Matches(string appKey, ProcessObservation observation)
    {
        if (appKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(observation.ExecutablePath))
            {
                return false;
            }

            return string.Equals(
                AppIdentity.NormalizeExecutablePath(appKey[4..]),
                AppIdentity.NormalizeExecutablePath(observation.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }

        if (appKey.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(observation.PackageFamilyName)
                && string.Equals(
                    appKey[4..],
                    observation.PackageFamilyName,
                    StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool StartTimeMatches(DateTimeOffset expected, long actualTicks)
    {
        if (actualTicks <= 0)
        {
            return false;
        }

        var delta = Math.Abs((new DateTimeOffset(actualTicks, TimeSpan.Zero) - expected).Ticks);
        return delta <= _startTimeTolerance.Ticks;
    }

    private static bool ExpectedIdentityMatches(
        GuardTerminationRequest request,
        ProcessObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(request.ExpectedPackageFamilyName))
        {
            return string.Equals(
                request.ExpectedPackageFamilyName,
                observation.PackageFamilyName,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedExecutablePath))
        {
            return !string.IsNullOrWhiteSpace(observation.ExecutablePath)
                && string.Equals(
                    AppIdentity.NormalizeExecutablePath(request.ExpectedExecutablePath),
                    AppIdentity.NormalizeExecutablePath(observation.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string BlockMessage(GuardProcessBlockReason reason) => reason switch
    {
        GuardProcessBlockReason.UncertainIdentity => "程序身份不确定，禁止结束。",
        GuardProcessBlockReason.SessionZero => "该进程运行在系统会话 0，禁止结束。",
        GuardProcessBlockReason.SystemPath => "该进程位于 Windows 系统目录，禁止结束。",
        GuardProcessBlockReason.ProtectedProcess => "该进程属于受保护的系统/桌面进程，禁止结束。",
        GuardProcessBlockReason.SharedService => "该进程是共享服务宿主，禁止结束。",
        GuardProcessBlockReason.PathUnavailable => "无法读取进程路径，无法确认身份，禁止结束。",
        _ => "该进程不满足安全结束条件。",
    };

    private static int GetSessionId(int processId)
    {
        return ProcessIdToSessionId((uint)processId, out var sessionId)
            ? (int)sessionId
            : -1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}