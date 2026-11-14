using System.IO;
using Microsoft.Win32;

namespace HotspotMeter.App.Services;

/// <summary>启动项当前的真实状态，而不是 UI 上次记住的状态。</summary>
public enum StartupRegistrationState
{
    /// <summary>没有名为 HotspotMeter 的启动项。</summary>
    NotRegistered,

    /// <summary>启动项存在，且指向当前正在运行的可执行文件。</summary>
    RegisteredForCurrentPath,

    /// <summary>启动项存在，但指向另一个路径（程序被移动/升级，或被其他软件改写）。</summary>
    RegisteredForOtherPath,

    /// <summary>读取注册表失败；此时不能假装“已关闭”或“已开启”。</summary>
    Unknown,
}

/// <summary>读取结果：状态 + 原始命令行（用于诊断与显示）。</summary>
public readonly record struct StartupRegistration(
    StartupRegistrationState State,
    string? Command,
    string? Error)
{
    public bool IsEnabledForCurrentPath => State == StartupRegistrationState.RegisteredForCurrentPath;
}

/// <summary>
/// 当前用户级开机自启动（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
///
/// 为什么用 HKCU Run：不需要管理员权限、随当前用户登录启动、部署方式仍是普通
/// 目录下的 exe；任务计划程序会引入额外权限与维护成本，MSIX 启动任务则要求
/// 打包部署，两者都与当前项目不符。
///
/// 安全边界：只操作值名 <c>HotspotMeter</c>。写入前不盲目覆盖用户放在同名值里的
/// 其他程序；关闭时也只删除“确实指向 HotspotMeter.exe”的值。路径始终带引号，
/// 因此 <c>C:\Program Files\...</c> 这类含空格路径不会被拆成多个参数。
/// </summary>
public sealed class StartupRegistrationService
{
    public const string ValueName = "HotspotMeter";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _executablePath;

    public StartupRegistrationService(string? executablePath = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;
    }

    public string ExecutablePath => _executablePath;

    public StartupRegistration Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return new StartupRegistration(StartupRegistrationState.NotRegistered, null, null);
            }

            var state = SameExecutable(value, _executablePath)
                ? StartupRegistrationState.RegisteredForCurrentPath
                : StartupRegistrationState.RegisteredForOtherPath;
            return new StartupRegistration(state, value, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new StartupRegistration(StartupRegistrationState.Unknown, null, ex.Message);
        }
    }

    /// <summary>
    /// 开启：写入带引号的当前 exe 路径。若同名值已存在且指向别的程序，
    /// 不覆盖，返回失败原因交由 UI 显示——删除/改写别人的配置不是本程序的权限。
    /// </summary>
    public StartupRegistration Enable()
    {
        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            return new StartupRegistration(StartupRegistrationState.Unknown, null, "无法确定当前程序路径。");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("无法打开当前用户启动项注册表。");

            if (key.GetValue(ValueName) is string existing &&
                !string.IsNullOrWhiteSpace(existing) &&
                !LooksLikeHotspotMeter(existing))
            {
                return new StartupRegistration(
                    StartupRegistrationState.RegisteredForOtherPath,
                    existing,
                    "同名启动项属于其他程序，未覆盖。请先手动处理该项。");
            }

            var command = Quote(_executablePath);
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return Read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new StartupRegistration(StartupRegistrationState.Unknown, null, ex.Message);
        }
    }

    /// <summary>
    /// 关闭：只删除指向 HotspotMeter.exe 的值。若用户/其他软件把同名值改成了别的
    /// 程序，保留它并在返回值里说明，避免误删他人启动项。
    /// </summary>
    public StartupRegistration Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string existing || string.IsNullOrWhiteSpace(existing))
            {
                return new StartupRegistration(StartupRegistrationState.NotRegistered, null, null);
            }

            if (!LooksLikeHotspotMeter(existing))
            {
                return new StartupRegistration(
                    StartupRegistrationState.RegisteredForOtherPath,
                    existing,
                    "同名启动项不属于 HotspotMeter，未删除。");
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return Read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new StartupRegistration(StartupRegistrationState.Unknown, null, ex.Message);
        }
    }

    /// <summary>
    /// 判断启动项是否是本程序：优先比较完整路径（忽略大小写与引号），
    /// 路径失效时退化为“文件名是 HotspotMeter.exe”，用于清理旧目录留下的项。
    /// </summary>
    private static bool LooksLikeHotspotMeter(string command)
    {
        var path = Unquote(command);
        if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(Path.GetFileName(path), "HotspotMeter.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameExecutable(string command, string executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath) &&
        string.Equals(Unquote(command), executablePath, StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string Quote(string path) => $"\"{path}\"";
}