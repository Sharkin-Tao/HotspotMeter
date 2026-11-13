using System.IO;

namespace HotspotMeter.App.Services;

/// <summary>
/// 极小的运行日志：只记录会影响用户体验的窗口行为（自动定位被跳过、任务栏重建等）。
/// <para>
/// 不接管异常（异常仍走 <see cref="CrashLog"/> 与全局处理器），
/// 也不能因为磁盘满/权限问题让 UI 崩溃，所以所有 IO 失败都静默。
/// </para>
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotspotMeter",
        "logs");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "app.log");

    internal static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);

                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}