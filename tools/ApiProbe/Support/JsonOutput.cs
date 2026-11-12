using System.Text.Encodings.Web;
using System.Text.Json;

namespace HotspotMeter.ApiProbe.Support;

/// <summary>把机器可读结果落盘，供文档引用与事后复核（不依赖控制台截图）。</summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, Options), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"JSON 已写入: {Path.GetFullPath(path)}");
    }

    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new ProbeException($"状态文件不存在: {Path.GetFullPath(path)}");
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}