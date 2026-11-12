using System.Windows;

namespace HotspotMeter.App;

/// <summary>
/// 应用程序入口。
/// <para>
/// 阶段 0 只用于验证 WPF 工具链可编译、可启动，不创建任何窗口。
/// 后续阶段将在这里完成：组合根（服务容器）、单实例检查、
/// 后台监控服务启动、悬浮状态栏与统计窗口的创建。
/// </para>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 阶段 0 没有任何窗口，且默认 ShutdownMode 是 OnLastWindowClose，
        // 不主动退出会留下一个没有界面的常驻进程。阶段 4 加入悬浮状态栏后删除此行。
        Shutdown();
    }
}