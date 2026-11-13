using System.IO;
using System.Text;

namespace TaskbarOverlayProbe;

/// <summary>
/// 简单文本日志：每行带本地时间，落盘到实验输出目录，便于报告引用。
/// </summary>
internal sealed class ProbeLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public ProbeLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "probe.log");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public string FilePath { get; }

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}