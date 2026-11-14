using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotspotMeter.App.Services;

/// <summary>
/// 悬浮胶囊的 UI 状态（位置、固定状态、尺寸、启动时是否显示）。
/// 采用可空字段承载后续版本新增的设置，保证旧版 ui-state.json 仍能读取。
/// </summary>
public sealed class CapsuleWindowState
{
    public double Left { get; set; }

    public double Top { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>标准尺寸为 320×104；为空表示旧版文件没有该字段。</summary>
    public double? Width { get; set; }

    public double? Height { get; set; }

    /// <summary>托盘隐藏胶囊后仍继续监控；该开关只控制启动时是否显示窗口。</summary>
    public bool? ShowCapsuleOnStartup { get; set; }

    /// <summary>设置窗口上次位置；为空时由窗口居中显示（旧文件没有这两个字段）。</summary>
    public double? SettingsLeft { get; set; }

    public double? SettingsTop { get; set; }

    /// <summary>自动适配任务栏。为空表示旧文件，按关闭处理（不改变用户已有布局）。</summary>
    public bool? AutoFitTaskbar { get; set; }

    /// <summary>
    /// 自动适配开启前的"手动尺寸/位置"。自动适配只改窗口，不覆盖这组值，
    /// 关闭自动适配时用它原样恢复，避免用户的手动配置被自动结果顶掉。
    /// </summary>
    public double? ManualLeft { get; set; }

    public double? ManualTop { get; set; }

    public double? ManualWidth { get; set; }

    public double? ManualHeight { get; set; }

    /// <summary>
    /// 统计窗口是否显示任务栏按钮。为空（旧文件）按开启处理：这是新功能的默认值，
    /// 但不能覆盖用户已经显式保存的 false。
    /// </summary>
    public bool? ShowStatsInTaskbar { get; set; }
}

/// <summary>
/// 悬浮窗状态存储。写入失败只影响"下次位置/尺寸"，不能让窗口或监控崩溃，
/// 因此所有 IO 异常都被吞掉。状态在内存里只保留一份，避免窗口与设置窗口互相覆盖字段。
/// </summary>
public sealed class WindowStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private CapsuleWindowState? _cached;
    private bool _loaded;

    public WindowStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotspotMeter",
            "ui-state.json");
    }

    /// <summary>状态文件路径；诊断输出会引用它，便于确认"是否真的落盘"。</summary>
    public string FilePath => _path;

    public CapsuleWindowState? Load()
    {
        if (_loaded)
        {
            return _cached;
        }

        _loaded = true;
        try
        {
            if (File.Exists(_path))
            {
                _cached = JsonSerializer.Deserialize<CapsuleWindowState>(File.ReadAllText(_path));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cached = null;
            // 损坏文件先改名保留，绝不能在下一次 Save 时被默认值静默覆盖。
            TryBackupBrokenFile(ex);
        }

        return _cached;
    }

    private void TryBackupBrokenFile(Exception error)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var backup = $"{_path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, backup);
            AppLog.Info($"ui-state.json 无法读取，已备份为 {backup}：{error.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"ui-state.json 无法读取且备份失败：{ex.Message}");
        }
    }

    public CapsuleWindowState GetOrCreate() => Load() ?? new CapsuleWindowState();

    /// <summary>更新单个字段并立即落盘；读取-修改-写入都在内存状态上完成，避免字段丢失。</summary>
    public void Update(Action<CapsuleWindowState> mutate)
    {
        var state = GetOrCreate();
        mutate(state);
        Save(state);
    }

    public void Save(CapsuleWindowState state)
    {
        _cached = state;
        _loaded = true;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再替换：撕裂写会留下半截 JSON，下一次读取就被当成“旧文件”，
            // 用户的尺寸/位置/自动适配设置会静默丢失。
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 位置/尺寸记忆不是核心功能，失败时保持当前会话内的布局即可。
            AppLog.Info($"ui-state.json 保存失败：{ex.Message}");
        }
    }
}