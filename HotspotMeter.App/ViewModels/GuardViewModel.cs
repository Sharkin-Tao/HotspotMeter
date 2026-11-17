using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using HotspotMeter.App.Localization;
using HotspotMeter.Core.Formatting;
using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.App.ViewModels;

/// <summary>程序筛选下拉项。Value 为 AppKey，null 表示全部程序。</summary>
public sealed class GuardFilterOption(string label, string? value)
{
    public string Label { get; } = label;

    public string? Value { get; } = value;

    // ComboBox 使用自定义 ControlTemplate，SelectionBoxItem 直接显示对象；
    // 覆写 ToString 让筛选器显示本地化标签而不是类型全名。
    public override string ToString() => Label;
}

/// <summary>历史列表的一行。列表所需的文案在构造时固化，语言切换时整体重建。</summary>
public sealed class GuardAlertListItem
{
    public required GuardAlertDetail Detail { get; init; }

    public required string TimeText { get; init; }

    public required string Name { get; init; }

    public required string BytesText { get; init; }

    public required string StatusText { get; init; }

    public required Brush StatusBrush { get; init; }

    public long AlertId => Detail.AlertId;
}

/// <summary>结束进程前的进程候选展示。</summary>
public sealed class GuardProcessItem
{
    public required GuardProcessCandidate Candidate { get; init; }

    public required string PidText { get; init; }

    public required string StartText { get; init; }

    public required string SafetyText { get; init; }

    public bool CanTerminate => Candidate.Safety.CanTerminate;

    public string PathText => string.IsNullOrWhiteSpace(Candidate.ExecutablePath)
        ? Candidate.PackageFamilyName ?? "—"
        : Candidate.ExecutablePath;
}

/// <summary>
/// 流量守护窗口的视图模型。所有操作都先写数据库、再从数据库重新读取，
/// 避免 UI 与规则表短暂不一致；任何一个操作失败都只影响当前窗口状态。
/// </summary>
public sealed class GuardViewModel : INotifyPropertyChanged
{
    private static CultureInfo Culture => LocalizationService.Current.Culture;

    private readonly IGuardManagementRepository _repository;
    private readonly IGuardProcessService _processService;

    private IReadOnlyList<GuardAlertDetail> _all = [];
    private GuardFilterOption? _selectedProgram;
    private GuardFilterOption? _selectedStatus;
    private GuardAlertListItem? _selectedAlert;
    private GuardProcessItem? _selectedProcess;
    private bool _suppressFilterReload;
    private bool _isBusy;
    private int _loadVersion;
    private int _detailVersion;
    private string _statusText = string.Empty;
    private string _detailName = "—";
    private string _detailIdentity = "—";
    private string _detailPath = "—";
    private string _detailPeriod = "—";
    private string _detailTime = "—";
    private string _detailThreshold = "—";
    private string _detailActual = "—";
    private string _detailDownload = "—";
    private string _detailUpload = "—";
    private string _detailActivity = "—";
    private string _detailNotification = "—";
    private string _detailHandling = "—";
    private string _detailRule = "—";
    private bool _hasDetail;
    private bool _hasProcesses;
    private string _emptyProcessText = string.Empty;

    public GuardViewModel(IGuardManagementRepository repository, IGuardProcessService processService)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));

        RefreshCommand = new AsyncCommand(() => LoadAsync());
        TrustCommand = new AsyncCommand(() => SetRuleAsync(GuardRuleMode.Trusted, GuardHandlingAction.Trusted), () => CanOperate);
        UntrustCommand = new AsyncCommand(() => SetRuleAsync(GuardRuleMode.Inherit, GuardHandlingAction.Untrusted), () => CanOperate);
        IgnoreHourCommand = new AsyncCommand(() => IgnoreAsync(TimeSpan.FromHours(1)), () => CanOperate);
        IgnoreDayCommand = new AsyncCommand(() => IgnoreAsync(TimeSpan.FromHours(24)), () => CanOperate);
        IgnoreWeekCommand = new AsyncCommand(() => IgnoreAsync(TimeSpan.FromDays(7)), () => CanOperate);
        OpenFolderCommand = new AsyncCommand(OpenFolderAsync, () => CanOperate);
        TerminateCommand = new AsyncCommand(TerminateSelectedAsync, () => CanOperate && SelectedProcess is { CanTerminate: true });

        RebuildStatusOptions();
        RebuildProgramOptions([]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<GuardAlertListItem> Alerts { get; } = [];

    public ObservableCollection<GuardProcessItem> Processes { get; } = [];

    public ObservableCollection<GuardFilterOption> ProgramOptions { get; } = [];

    public ObservableCollection<GuardFilterOption> StatusOptions { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand TrustCommand { get; }

    public ICommand UntrustCommand { get; }

    public ICommand IgnoreHourCommand { get; }

    public ICommand IgnoreDayCommand { get; }

    public ICommand IgnoreWeekCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand TerminateCommand { get; }

    public GuardFilterOption? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (!SetField(ref _selectedProgram, value) || _suppressFilterReload)
            {
                return;
            }

            ApplyFilters();
        }
    }

    public GuardFilterOption? SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (!SetField(ref _selectedStatus, value) || _suppressFilterReload)
            {
                return;
            }

            ApplyFilters();
        }
    }

    public GuardAlertListItem? SelectedAlert
    {
        get => _selectedAlert;
        set
        {
            if (!SetField(ref _selectedAlert, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanOperate));
            if (value is null)
            {
                ClearDetail();
                return;
            }

            _ = LoadDetailAsync(value.AlertId);
        }
    }

    public GuardProcessItem? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool CanOperate => !IsBusy && _hasDetail && _selectedAlert is not null;

    public bool HasDetail
    {
        get => _hasDetail;
        private set => SetField(ref _hasDetail, value);
    }

    public bool HasProcesses
    {
        get => _hasProcesses;
        private set => SetField(ref _hasProcesses, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string EmptyProcessText
    {
        get => _emptyProcessText;
        private set => SetField(ref _emptyProcessText, value);
    }

    public string DetailName
    {
        get => _detailName;
        private set => SetField(ref _detailName, value);
    }

    public string DetailIdentity
    {
        get => _detailIdentity;
        private set => SetField(ref _detailIdentity, value);
    }

    public string DetailPath
    {
        get => _detailPath;
        private set => SetField(ref _detailPath, value);
    }

    public string DetailPeriod
    {
        get => _detailPeriod;
        private set => SetField(ref _detailPeriod, value);
    }

    public string DetailTime
    {
        get => _detailTime;
        private set => SetField(ref _detailTime, value);
    }

    public string DetailThreshold
    {
        get => _detailThreshold;
        private set => SetField(ref _detailThreshold, value);
    }

    public string DetailActual
    {
        get => _detailActual;
        private set => SetField(ref _detailActual, value);
    }

    public string DetailDownload
    {
        get => _detailDownload;
        private set => SetField(ref _detailDownload, value);
    }

    public string DetailUpload
    {
        get => _detailUpload;
        private set => SetField(ref _detailUpload, value);
    }

    public string DetailActivity
    {
        get => _detailActivity;
        private set => SetField(ref _detailActivity, value);
    }

    public string DetailNotification
    {
        get => _detailNotification;
        private set => SetField(ref _detailNotification, value);
    }

    public string DetailHandling
    {
        get => _detailHandling;
        private set => SetField(ref _detailHandling, value);
    }

    public string DetailRule
    {
        get => _detailRule;
        private set => SetField(ref _detailRule, value);
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsBusy = true;
        StatusText = LocalizationService.T("Guard_Loading");
        try
        {
            var details = await _repository.GetAlertDetailsAsync(new GuardHistoryQuery(Limit: 500)).ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            _all = details;
            RebuildProgramOptions(details);
            ApplyFilters();
        }
        catch (Exception exception)
        {
            if (version == _loadVersion)
            {
                _all = [];
                Alerts.Clear();
                ClearDetail();
                StatusText = LocalizationService.F("Guard_LoadFailed", exception.Message);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    public async Task TerminateSelectedAsync()
    {
        if (_selectedAlert is null || SelectedProcess is not { CanTerminate: true } process)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var request = new GuardTerminationRequest(
                process.Candidate.AppKey,
                process.Candidate.ProcessId,
                process.Candidate.StartedAtUtc,
                process.Candidate.ExecutablePath,
                process.Candidate.PackageFamilyName);
            var result = await _processService.TerminateAsync(request).ConfigureAwait(true);
            if (result.Status == GuardProcessActionStatus.Success)
            {
                await _repository.RecordHandlingAsync(
                    _selectedAlert.AlertId,
                    GuardHandlingAction.Terminated,
                    DateTimeOffset.UtcNow).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
                StatusText = LocalizationService.T("Guard_TerminateSuccess");
            }
            else
            {
                StatusText = DescribeTermination(result);
            }
        }
        catch (Exception exception)
        {
            StatusText = LocalizationService.F("Guard_ActionFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>语言切换时重建所有可见文案，并重新读取一次数据。</summary>
    public void RefreshLanguage()
    {
        RebuildStatusOptions();
        RebuildProgramOptions(_all);
        ClearDetail();
        _ = LoadAsync();
    }

    private async Task IgnoreAsync(TimeSpan duration)
    {
        if (_selectedAlert is null)
        {
            return;
        }

        var until = DateTimeOffset.UtcNow.Add(duration);
        await SetRuleAsync(GuardRuleMode.Ignored, GuardHandlingAction.Ignored, until).ConfigureAwait(true);
    }

    private async Task SetRuleAsync(
        GuardRuleMode mode,
        GuardHandlingAction action,
        DateTimeOffset? ignoreUntil = null)
    {
        if (_selectedAlert is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var detail = _selectedAlert.Detail;
            var existing = await _repository.GetAppRuleAsync(detail.AppKey).ConfigureAwait(true);
            var rule = new GuardAppRule(
                detail.AppKey,
                mode,
                existing?.ThresholdOverrideBytes,
                mode == GuardRuleMode.Ignored ? ignoreUntil : null,
                existing?.ExecutablePath ?? detail.ExecutablePath);

            // 先写规则，再记录处理动作；任何一步失败都不会把界面改成“成功”。
            await _repository.SaveAppRuleAsync(rule).ConfigureAwait(true);
            await _repository.RecordHandlingAsync(detail.AlertId, action, DateTimeOffset.UtcNow).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            StatusText = LocalizationService.T(action switch
            {
                GuardHandlingAction.Trusted => "Guard_RuleTrusted",
                GuardHandlingAction.Untrusted => "Guard_RuleInherited",
                _ => "Guard_RuleIgnored",
            });
        }
        catch (Exception exception)
        {
            StatusText = LocalizationService.F("Guard_ActionFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenFolderAsync()
    {
        if (_selectedAlert is null)
        {
            return;
        }

        var detail = _selectedAlert.Detail;
        try
        {
            var path = detail.ExecutablePath;
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                StatusText = LocalizationService.T("Guard_PathUnavailable");
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }

            await _repository.RecordHandlingAsync(
                detail.AlertId,
                GuardHandlingAction.OpenedFolder,
                DateTimeOffset.UtcNow).ConfigureAwait(true);
            StatusText = LocalizationService.T("Guard_FolderOpened");
        }
        catch (Exception exception)
        {
            StatusText = LocalizationService.F("Guard_ActionFailed", exception.Message);
        }
    }

    private async Task LoadDetailAsync(long alertId)
    {
        var version = ++_detailVersion;
        try
        {
            var detail = await _repository.GetAlertDetailAsync(alertId).ConfigureAwait(true);
            if (version != _detailVersion)
            {
                return;
            }

            if (detail is null)
            {
                ClearDetail();
                return;
            }

            FillDetail(detail);

            var processes = await _processService.GetProcessesAsync(detail.AppKey).ConfigureAwait(true);
            if (version != _detailVersion)
            {
                return;
            }

            Processes.Clear();
            foreach (var candidate in processes)
            {
                Processes.Add(BuildProcessItem(candidate));
            }

            HasProcesses = Processes.Count > 0;
            EmptyProcessText = processes.Count == 0
                ? LocalizationService.T("Guard_NoProcesses")
                : string.Empty;
            SelectedProcess = Processes.FirstOrDefault(item => item.CanTerminate) ?? Processes.FirstOrDefault();
        }
        catch (Exception exception)
        {
            if (version == _detailVersion)
            {
                StatusText = LocalizationService.F("Guard_LoadFailed", exception.Message);
            }
        }
        finally
        {
            if (version == _detailVersion)
            {
                RaiseCommandStates();
            }
        }
    }

    private void FillDetail(GuardAlertDetail detail)
    {
        HasDetail = true;
        DetailName = string.IsNullOrWhiteSpace(detail.DisplayName) ? detail.AppKey : detail.DisplayName;
        DetailIdentity = LocalizationService.T(detail.IdentityKind switch
        {
            AppIdentityKind.DesktopExecutable => "Guard_IdentityExe",
            AppIdentityKind.Packaged => "Guard_IdentityPackage",
            _ => "Guard_IdentityUncertain",
        }) + (detail.IdentityCertain ? string.Empty : $" · {LocalizationService.T("Guard_Uncertain")}");

        DetailPath = string.IsNullOrWhiteSpace(detail.ExecutablePath) ? "—" : detail.ExecutablePath;
        DetailPeriod = PeriodText(detail.Period);
        DetailTime = detail.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Culture);
        DetailThreshold = ByteSizeFormatter.Format(detail.ThresholdBytes, Culture)
            + " · " + LocalizationService.T(detail.ThresholdFromAppRule ? "Guard_ThresholdApp" : "Guard_ThresholdGlobal");
        DetailActual = ByteSizeFormatter.Format(detail.ActualBytes, Culture);
        DetailDownload = ByteSizeFormatter.Format(detail.DownloadBytes, Culture);
        DetailUpload = ByteSizeFormatter.Format(detail.UploadBytes, Culture);
        DetailActivity = LocalizationService.T(detail.ActivityState switch
        {
            GuardActivityState.Foreground => "Guard_StateForeground",
            GuardActivityState.Background => "Guard_StateBackground",
            _ => "Guard_StateIdle",
        });
        DetailNotification = LocalizationService.T(detail.NotificationStatus switch
        {
            GuardNotificationStatus.Sent => "Guard_NotificationSent",
            GuardNotificationStatus.Suppressed => "Guard_NotificationSuppressed",
            GuardNotificationStatus.Failed => "Guard_NotificationFailed",
            _ => "Guard_NotificationPending",
        }) + (string.IsNullOrWhiteSpace(detail.NotificationError) ? string.Empty : $" · {detail.NotificationError}");

        DetailHandling = detail.HandlingAction == GuardHandlingAction.None
            ? LocalizationService.T("Guard_HandlingNone")
            : LocalizationService.T(DetailHandlingKey(detail.HandlingAction))
              + (detail.HandledAtUtc is { } at
                  ? " · " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Culture)
                  : string.Empty);

        var ruleMode = LocalizationService.T(detail.RuleMode switch
        {
            GuardRuleMode.Trusted => "Guard_RuleModeTrusted",
            GuardRuleMode.Disabled => "Guard_RuleModeDisabled",
            GuardRuleMode.Ignored => "Guard_RuleModeIgnored",
            _ => "Guard_RuleModeInherit",
        });
        DetailRule = detail.RuleMode == GuardRuleMode.Ignored && detail.IgnoreUntilUtc is { } until
            ? $"{ruleMode} · {LocalizationService.F("Guard_Until", until.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Culture))}"
            : ruleMode;

        RaiseCommandStates();
    }

    private void ClearDetail()
    {
        HasDetail = false;
        HasProcesses = false;
        Processes.Clear();
        SelectedProcess = null;
        DetailName = "—";
        DetailIdentity = "—";
        DetailPath = "—";
        DetailPeriod = "—";
        DetailTime = "—";
        DetailThreshold = "—";
        DetailActual = "—";
        DetailDownload = "—";
        DetailUpload = "—";
        DetailActivity = "—";
        DetailNotification = "—";
        DetailHandling = "—";
        DetailRule = "—";
        RaiseCommandStates();
    }

    private void ApplyFilters()
    {
        if (_suppressFilterReload)
        {
            return;
        }

        var program = SelectedProgram?.Value;
        var status = SelectedStatus?.Value is { } raw && int.TryParse(raw, out var parsed)
            ? (GuardNotificationStatus?)parsed
            : null;

        var filtered = _all
            .Where(item => program is null || string.Equals(item.AppKey, program, StringComparison.Ordinal))
            .Where(item => status is null || item.NotificationStatus == status)
            .ToArray();

        var previousId = _selectedAlert?.AlertId;
        Alerts.Clear();
        foreach (var detail in filtered)
        {
            Alerts.Add(BuildAlertItem(detail));
        }

        StatusText = filtered.Length == 0
            ? LocalizationService.T("Guard_Empty")
            : LocalizationService.F("Guard_Count", filtered.Length);

        var next = previousId is { } id
            ? Alerts.FirstOrDefault(item => item.AlertId == id)
            : null;
        next ??= Alerts.FirstOrDefault();
        SelectedAlert = next;
        if (next is null)
        {
            ClearDetail();
        }
    }

    private GuardAlertListItem BuildAlertItem(GuardAlertDetail detail) => new()
    {
        Detail = detail,
        TimeText = detail.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm", Culture),
        Name = string.IsNullOrWhiteSpace(detail.DisplayName) ? detail.AppKey : detail.DisplayName,
        BytesText = ByteSizeFormatter.Format(detail.ActualBytes, Culture),
        StatusText = LocalizationService.T(detail.NotificationStatus switch
        {
            GuardNotificationStatus.Sent => "Guard_NotificationSent",
            GuardNotificationStatus.Suppressed => "Guard_NotificationSuppressed",
            GuardNotificationStatus.Failed => "Guard_NotificationFailed",
            _ => "Guard_NotificationPending",
        }),
        StatusBrush = detail.NotificationStatus switch
        {
            GuardNotificationStatus.Sent => new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
            GuardNotificationStatus.Failed => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            GuardNotificationStatus.Suppressed => new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x3A)),
            _ => new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)),
        },
    };

    private GuardProcessItem BuildProcessItem(GuardProcessCandidate candidate) => new()
    {
        Candidate = candidate,
        PidText = candidate.ProcessId.ToString(CultureInfo.InvariantCulture),
        StartText = candidate.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Culture),
        SafetyText = candidate.Safety.CanTerminate
            ? LocalizationService.T("Guard_CanTerminate")
            : LocalizationService.T(BlockReasonKey(candidate.Safety.Reason)),
    };

    private void RebuildProgramOptions(IReadOnlyList<GuardAlertDetail> details)
    {
        var previous = SelectedProgram?.Value;
        _suppressFilterReload = true;
        try
        {
            ProgramOptions.Clear();
            ProgramOptions.Add(new GuardFilterOption(LocalizationService.T("Guard_AllPrograms"), null));
            foreach (var group in details
                         .GroupBy(item => item.AppKey, StringComparer.Ordinal)
                         .Select(group => group.OrderByDescending(item => item.CreatedAtUtc).First())
                         .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var name = string.IsNullOrWhiteSpace(group.DisplayName) ? group.AppKey : group.DisplayName;
                ProgramOptions.Add(new GuardFilterOption(name, group.AppKey));
            }

            _selectedProgram = ProgramOptions.FirstOrDefault(item => item.Value == previous) ?? ProgramOptions[0];
            OnPropertyChanged(nameof(SelectedProgram));
        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    private void RebuildStatusOptions()
    {
        var previous = SelectedStatus?.Value;
        _suppressFilterReload = true;
        try
        {
            StatusOptions.Clear();
            StatusOptions.Add(new GuardFilterOption(LocalizationService.T("Guard_AllStatuses"), null));
            StatusOptions.Add(new GuardFilterOption(LocalizationService.T("Guard_NotificationSent"), "1"));
            StatusOptions.Add(new GuardFilterOption(LocalizationService.T("Guard_NotificationSuppressed"), "2"));
            StatusOptions.Add(new GuardFilterOption(LocalizationService.T("Guard_NotificationFailed"), "3"));
            StatusOptions.Add(new GuardFilterOption(LocalizationService.T("Guard_NotificationPending"), "0"));
            _selectedStatus = StatusOptions.FirstOrDefault(item => item.Value == previous) ?? StatusOptions[0];
            OnPropertyChanged(nameof(SelectedStatus));
        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    private static string PeriodText(GuardPeriod period) => LocalizationService.T(period switch
    {
        GuardPeriod.Session => "Guard_PeriodSession",
        GuardPeriod.Rolling24Hours => "Guard_PeriodRolling24h",
        _ => "Guard_PeriodToday",
    });

    private static string DetailHandlingKey(GuardHandlingAction action) => action switch
    {
        GuardHandlingAction.Trusted => "Guard_HandlingTrusted",
        GuardHandlingAction.Untrusted => "Guard_HandlingUntrusted",
        GuardHandlingAction.Ignored => "Guard_HandlingIgnored",
        GuardHandlingAction.OpenedFolder => "Guard_HandlingOpened",
        GuardHandlingAction.Terminated => "Guard_HandlingTerminated",
        _ => "Guard_HandlingNone",
    };

    private static string BlockReasonKey(GuardProcessBlockReason reason) => reason switch
    {
        GuardProcessBlockReason.UncertainIdentity => "Guard_BlockUncertain",
        GuardProcessBlockReason.SessionZero => "Guard_BlockSessionZero",
        GuardProcessBlockReason.SystemPath => "Guard_BlockSystemPath",
        GuardProcessBlockReason.ProtectedProcess => "Guard_BlockProtected",
        GuardProcessBlockReason.SharedService => "Guard_BlockSharedService",
        GuardProcessBlockReason.PathUnavailable => "Guard_BlockNoPath",
        _ => "Guard_BlockUnknown",
    };

    private string DescribeTermination(GuardTerminationResult result) => result.Status switch
    {
        GuardProcessActionStatus.AlreadyExited => LocalizationService.T("Guard_TerminateExited"),
        GuardProcessActionStatus.AccessDenied => LocalizationService.T("Guard_TerminateDenied"),
        GuardProcessActionStatus.IdentityChanged => LocalizationService.T("Guard_TerminateIdentity"),
        GuardProcessActionStatus.Blocked => LocalizationService.T(BlockReasonKey(result.BlockReason)),
        GuardProcessActionStatus.PathUnavailable => LocalizationService.T("Guard_BlockNoPath"),
        _ => LocalizationService.F("Guard_TerminateFailed", result.Detail ?? string.Empty),
    };

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanOperate));
        ((AsyncCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)TrustCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)UntrustCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)IgnoreHourCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)IgnoreDayCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)IgnoreWeekCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenFolderCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)TerminateCommand).RaiseCanExecuteChanged();
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
    {
        private bool _running;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (_running || !CanExecute(parameter))
            {
                return;
            }

            _running = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}



