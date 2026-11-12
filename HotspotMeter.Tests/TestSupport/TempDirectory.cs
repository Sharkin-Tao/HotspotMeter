namespace HotspotMeter.Tests.TestSupport;

/// <summary>
/// 每个测试独立的临时目录。测试结束递归删除，避免把数据库文件留在仓库或 %TEMP% 根部。
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? prefix = null)
    {
        var name = $"{prefix ?? "hotspotmeter-test"}-{Guid.NewGuid():N}";
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hotspotmeter-tests", name);
        Directory.CreateDirectory(Path);
    }

    /// <summary>目录绝对路径。</summary>
    public string Path { get; }

    /// <summary>在该目录下生成一个文件路径（不创建文件）。</summary>
    public string File(string fileName) => System.IO.Path.Combine(Path, fileName);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不应掩盖真正的断言失败；残留目录位于 %TEMP%，系统会回收。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}