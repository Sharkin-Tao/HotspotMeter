using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HotspotMeter.App.Services;
using HotspotMeter.Core.Formatting;

namespace HotspotMeter.App.ViewModels;

/// <summary>柱状图中的一天。高度按本周最大值归一化，无记录与真实 0 用不同文案区分。</summary>
public sealed class DailyBarItem
{
    public required DateOnly Date { get; init; }

    public required string DateLabel { get; init; }

    public required string WeekdayLabel { get; init; }

    public required string TotalText { get; init; }

    public required string DetailText { get; init; }

    public required double DownloadHeight { get; init; }

    public required double UploadHeight { get; init; }

    public required bool HasRecord { get; init; }

    public bool IsToday { get; init; }

    public string EmptyText => HasRecord ? string.Empty : "无数据";
}

/// <summary>程序排行的一行。ShareText 只在分母明确（已归属总量）时显示。</summary>
public sealed class ProgramUsageItem
{
    public required string Name { get; init; }

    public required string DownloadText { get; init; }

    public required string UploadText { get; init; }

    public required string TotalText { get; init; }

    public required string ShareText { get; init; }

    public required bool IsUnattributed { get; init; }

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
}

public enum StatsScope
{
    Day,
    LastSevenDays,
}

/// <summary>
/// 统计窗口的视图模型。所有数据来自本机 SQLite 与 Windows 归属 API；
/// 无记录时显示“无数据”，而不是 0；逐应用不可用时只显示明确状态，不生成假排行。
/// </summary>
public sealed class StatsViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private readonly TrafficStatsService _statsService;
    private readonly AttributedUsageReader _usageReader;
    private readonly Func<(Guid? AdapterId, string? Ssid)> _targetProvider;

    private DateTime? _selectedDate = DateTime.Today;
    private StatsScope _scope = StatsScope.Day;
    private bool _isBusy;
    private string _statusText = "正在读取历史数据…";
    private string _errorText = string.Empty;
    private string _rangeTitle = "今日";
    private string _totalText = "—";
    private string _downloadText = "—";
    private string _uploadText = "—";
    private string _updateText = "—";
    private string _programStatus = "正在读取逐应用数据…";
    private string _dateQueryText = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private int _loadVersion;

    public StatsViewModel(
        TrafficStatsService statsService,
        AttributedUsageReader usageReader,
        Func<(Guid? AdapterId, string? Ssid)> targetProvider)
    {
        _statsService = statsService;
        _usageReader = usageReader;
        _targetProvider = targetProvider;

        RefreshCommand = new AsyncCommand(LoadAsync);
        SelectTodayCommand = new AsyncCommand(() => SelectDateAsync(DateTime.Today));
        SelectSevenDaysCommand = new AsyncCommand(() => SelectScopeAsync(StatsScope.LastSevenDays));
        QueryDateCommand = new AsyncCommand(QueryDateAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DailyBarItem> Days { get; } = [];

    public ObservableCollection<ProgramUsageItem> Programs { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand SelectTodayCommand { get; }

    public ICommand SelectSevenDaysCommand { get; }

    public ICommand QueryDateCommand { get; }

    /// <summary>手输日期查询。格式错误只提示，不改变当前已展示的数据。</summary>
    public string DateQueryText
    {
        get => _dateQueryText;
        set => SetField(ref _dateQueryText, value);
    }

    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate == value)
            {
                return;
            }

            _selectedDate = value;
            OnPropertyChanged();
            _ = SelectDateAsync(value);
        }
    }

    public string RangeTitle
    {
        get => _rangeTitle;
        private set => SetField(ref _rangeTitle, value);
    }

    public string TotalText
    {
        get => _totalText;
        private set => SetField(ref _totalText, value);
    }

    public string DownloadText
    {
        get => _downloadText;
        private set => SetField(ref _downloadText, value);
    }

    public string UploadText
    {
        get => _uploadText;
        private set => SetField(ref _uploadText, value);
    }

    public string UpdateText
    {
        get => _updateText;
        private set => SetField(ref _updateText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetField(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string ProgramStatus
    {
        get => _programStatus;
        private set => SetField(ref _programStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsBusy = true;
        ErrorText = string.Empty;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var seven = await _statsService.GetLastSevenDaysAsync();
            if (version != _loadVersion)
            {
                return;
            }

            BuildBars(seven, today);
            await LoadSummaryAsync(version);
            await LoadProgramsAsync(version);

            StatusText = _statsService.DatabaseExists
                ? "数据来自本机热点会话检查点；不是运营商计费流量。"
                : "尚未生成历史数据库：连接热点并保持程序运行后会出现记录。";
            UpdateText = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ErrorText = $"历史数据读取失败：{ex.Message}";
            StatusText = "可点击刷新重试；后台监控不受影响。";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task SelectScopeAsync(StatsScope scope)
    {
        _scope = scope;
        await LoadAsync();
    }

    private async Task QueryDateAsync()
    {
        if (!DateTime.TryParseExact(
                DateQueryText.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            ErrorText = "日期格式应为 yyyy-MM-dd，例如 2026-11-14。";
            return;
        }

        if (date.Date > DateTime.Today)
        {
            ErrorText = "不能查询未来的日期。";
            return;
        }

        ErrorText = string.Empty;
        // 直接写字段，避免 SelectedDate setter 再触发一次 LoadAsync（版本号会作废前一次查询）。
        _selectedDate = date.Date;
        OnPropertyChanged(nameof(SelectedDate));
        await LoadAsync();
    }

    private async Task SelectDateAsync(DateTime? value)
    {
        if (value is null)
        {
            return;
        }

        _scope = StatsScope.Day;
        await LoadAsync();
    }

    private async Task LoadSummaryAsync(int version)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        IReadOnlyList<DailyUsage> rows;
        if (_scope == StatsScope.LastSevenDays)
        {
            rows = await _statsService.GetDailyAsync(today.AddDays(-6), today);
            RangeTitle = "最近 7 天";
        }
        else
        {
            var date = DateOnly.FromDateTime(SelectedDate ?? DateTime.Today);
            rows = await _statsService.GetDailyAsync(date, date);
            RangeTitle = date == today ? "今日" : date.ToString("yyyy-MM-dd", Culture);
        }

        if (version != _loadVersion)
        {
            return;
        }

        var recorded = rows.Where(r => r.HasRecord).ToArray();
        if (recorded.Length == 0)
        {
            TotalText = "无数据";
            DownloadText = "无数据";
            UploadText = "无数据";
            return;
        }

        var download = recorded.Sum(r => r.DownloadBytes);
        var upload = recorded.Sum(r => r.UploadBytes);
        TotalText = ByteSizeFormatter.Format(download + upload, Culture);
        DownloadText = ByteSizeFormatter.Format(download, Culture);
        UploadText = ByteSizeFormatter.Format(upload, Culture);
    }

    private async Task LoadProgramsAsync(int version)
    {
        Programs.Clear();
        ProgramStatus = "正在读取逐应用数据…";

        var today = DateOnly.FromDateTime(DateTime.Today);
        var dates = _scope == StatsScope.LastSevenDays
            ? Enumerable.Range(0, 7).Select(offset => today.AddDays(offset - 6)).ToArray()
            : [DateOnly.FromDateTime(SelectedDate ?? DateTime.Today)];
        var (adapterId, ssid) = _targetProvider();

        // 逐应用 API 按“单日区间”查询；最近 7 天需要逐日取回后按 AttributionId 合并。
        // 不能拿总量减已归属来伪造应用行，差额只作为“未归属”说明。
        var merged = new Dictionary<string, ProgramUsage>(StringComparer.OrdinalIgnoreCase);
        long totalDownload = 0;
        long totalUpload = 0;
        var availableDays = 0;
        string? firstFailure = null;

        foreach (var date in dates)
        {
            var result = await _usageReader.ReadAsync(date, adapterId, ssid);
            if (version != _loadVersion)
            {
                return;
            }

            if (!result.IsAvailable)
            {
                firstFailure ??= result.Status;
                continue;
            }

            availableDays++;
            totalDownload += result.TotalDownloadBytes;
            totalUpload += result.TotalUploadBytes;

            foreach (var program in result.Programs)
            {
                var key = string.IsNullOrWhiteSpace(program.AttributionId)
                    ? "__unattributed__"
                    : program.AttributionId;
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = existing with
                    {
                        DownloadBytes = existing.DownloadBytes + program.DownloadBytes,
                        UploadBytes = existing.UploadBytes + program.UploadBytes,
                    };
                }
                else
                {
                    merged[key] = program;
                }
            }
        }

        if (availableDays == 0)
        {
            ProgramStatus = firstFailure ?? "逐应用流量不可用。";
            return;
        }

        var ordered = merged.Values
            .Where(item => item.TotalBytes > 0)
            .OrderByDescending(item => item.TotalBytes)
            .ToArray();
        var listedTotal = ordered.Sum(item => item.TotalBytes);
        foreach (var program in ordered)
        {
            Programs.Add(new ProgramUsageItem
            {
                Name = program.DisplayName,
                DownloadText = ByteSizeFormatter.Format(program.DownloadBytes, Culture),
                UploadText = ByteSizeFormatter.Format(program.UploadBytes, Culture),
                TotalText = ByteSizeFormatter.Format(program.TotalBytes, Culture),
                ShareText = listedTotal > 0
                    ? $"{program.TotalBytes * 100.0 / listedTotal:0.0}%"
                    : "—",
                IsUnattributed = program.IsUnattributed,
            });
        }

        var scopeLabel = _scope == StatsScope.LastSevenDays
            ? "最近 7 天"
            : dates[0].ToString("yyyy-MM-dd", Culture);
        var unattributed = Math.Max(0, totalDownload + totalUpload - listedTotal);
        ProgramStatus = ordered.Length == 0
            ? $"{scopeLabel}没有可用的逐应用归属记录；系统可能尚未生成统计。"
            : $"系统归属数据：{ordered.Length} 项（{scopeLabel}）；占比按列表合计；未归属差额 {ByteSizeFormatter.Format(unattributed, Culture)}。";
    }

    private void BuildBars(IReadOnlyList<DailyUsage> rows, DateOnly today)
    {
        Days.Clear();
        var max = rows.Count == 0 ? 0 : rows.Max(r => r.TotalBytes);
        const double BarArea = 150;

        foreach (var row in rows)
        {
            var total = row.TotalBytes;
            var scale = max <= 0 ? 0 : BarArea / max;
            Days.Add(new DailyBarItem
            {
                Date = row.Date,
                DateLabel = row.Date.ToString("M/d", Culture),
                WeekdayLabel = row.Date.ToString("ddd", Culture),
                TotalText = row.HasRecord ? ByteSizeFormatter.Format(total, Culture) : "无数据",
                DetailText = row.HasRecord
                    ? $"↓ {ByteSizeFormatter.Format(row.DownloadBytes, Culture)}  ↑ {ByteSizeFormatter.Format(row.UploadBytes, Culture)}"
                    : "当天没有检查点",
                DownloadHeight = Math.Max(0, row.DownloadBytes * scale),
                UploadHeight = Math.Max(0, row.UploadBytes * scale),
                HasRecord = row.HasRecord,
                IsToday = row.Date == today,
            });
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>防止连续点击刷新产生并发查询；执行期间命令自身不可重入。</summary>
    private sealed class AsyncCommand(Func<Task> execute) : ICommand
    {
        private bool _running;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !_running;

        public async void Execute(object? parameter)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute();
            }
            finally
            {
                _running = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}