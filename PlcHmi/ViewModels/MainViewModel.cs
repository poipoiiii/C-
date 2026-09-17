using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcCore.Services;
using PlcHmi.Models;
using PlcHmi.Services;
using SkiaSharp;

namespace PlcHmi.ViewModels;

/// <summary>
/// 看板主视图模型：连接与采集控制、实时点位与曲线、点位扫描配置、存储映射、数据库记录。
/// 实时数据来自 Modbus（内存），持久化与查询按「存储映射」走动态表 —— 双数据源。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>最近记录区每次从数据库取多少条</summary>
    private const int RecentRecordCount = 50;

    /// <summary>从站探测范围（协议里 UnitId 是 0~247，0 一般作广播，现场多从 1 开始且很少超过 32）</summary>
    private const byte ProbeStartId = 1;
    private const byte ProbeEndId = 32;

    /// <summary>单个从站的探测超时：短一点，避免探测一轮等太久</summary>
    private const int ProbeTimeoutMs = 400;

    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#E6A23C"),
        SKColor.Parse("#409EFF"),
        SKColor.Parse("#67C23A"),
        SKColor.Parse("#F56C6C"),
        SKColor.Parse("#9B59B6"),
        SKColor.Parse("#909399")
    ];

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _recordTimer;
    private readonly PlcConfig _config;
    private readonly ModbusScanService _scanService = new();
    private readonly SlaveProbeService _probeService = new();
    private readonly DynamicTableStore _tableStore = new();
    private readonly TriggerLogStore _triggerStore = new();

    private ModbusService? _modbus;
    private PlcAcquisitionService? _acquisition;
    private TriggerWatchService? _triggerWatch;
    private CancellationTokenSource? _scanCts;

    // ---------------- 连接与采集 ----------------

    [ObservableProperty] private string _ip = "127.0.0.1";
    [ObservableProperty] private int _port = 5020;
    [ObservableProperty] private byte _slaveId = 1;

    /// <summary>
    /// 从站号输入框绑的是文本，不是 byte：直接绑 byte 的话边打字边转换，
    /// 中间态（比如只打了半个数字）会弹红色的“未能转换值”校验错误。
    /// </summary>
    [ObservableProperty] private string _slaveIdText = "1";
    [ObservableProperty] private int _intervalMs = 1000;
    [ObservableProperty] private bool _persistEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isAcquiring;

    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private string _commError = string.Empty;
    [ObservableProperty] private string _persistError = string.Empty;
    [ObservableProperty] private string _recordsInfo = "未加载";

    // ---------------- 点位扫描 ----------------

    [ObservableProperty] private string _scanStart = "40001";
    [ObservableProperty] private string _scanEnd = "40040";
    [ObservableProperty] private int _scanBatchSize = 10;
    [ObservableProperty] private string _scanStatus = "未扫描";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _tagStatus = "";

    // ---------------- 存储映射 ----------------

    /// <summary>界面上的组表草稿，点【建表 / 校验结构】或【保存配置】才提交成生效配置</summary>
    public ObservableCollection<StorageGroup> Groups { get; }

    /// <summary>组名清单，供界面展示</summary>
    public ObservableCollection<string> GroupNames { get; } = [];

    [ObservableProperty] private StorageGroup? _selectedGroup;
    [ObservableProperty] private string _storageStatus = "";
    [ObservableProperty] private string _createScriptPreview = "";
    [ObservableProperty] private string _effectiveStorageText = "";
    [ObservableProperty] private DataView? _recentView;

    // ---------------- 触发记录（握手模拟上升沿） ----------------

    /// <summary>触发记录规则（直接就是配置对象里的集合）</summary>
    public ObservableCollection<TriggerRule> Triggers { get; }

    [ObservableProperty] private TriggerRule? _selectedTrigger;

    /// <summary>信号轮询间隔（毫秒）：触发频繁就调小，默认 100</summary>
    [ObservableProperty] private int _triggerPollIntervalMs = 100;

    [ObservableProperty] private string _triggerStatus = "未监听";
    [ObservableProperty] private string _triggerAlarm = string.Empty;
    [ObservableProperty] private int _triggerCount;
    [ObservableProperty] private DataView? _triggerView;
    [ObservableProperty] private string _triggerRecordsInfo = "未加载";

    private readonly HashSet<string> _stuckRules = [];

    public MainViewModel()
    {
        _config = ConfigStore.Load();
        ReconcileDefaultColumnNames(_config.Tags);
        TagConfigs = _config.Tags;
        Triggers = _config.Triggers;

        // 组表在界面上是草稿，改完要点【建表 / 校验结构】或【保存配置】才提交
        Groups = [.. _config.Groups.Select(g => g.Clone())];
        Groups.CollectionChanged += OnGroupsChanged;
        foreach (var group in Groups)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }

        RefreshGroupNames();
        RefreshTagGroups();
        SelectedGroup = Groups.FirstOrDefault();

        ModeOptions =
        [
            new StorageModeOption(StorageMode.Long, "长表：一行一个点位（时间/地址/原始值/工程值）"),
            new StorageModeOption(StorageMode.Wide, "宽表：一行一个时刻（时间 + 每个点位一列）")
        ];

        RebuildTags();
        AttachTagSubscriptions();
        UpdateScriptPreview();
        UpdateEffectiveStorageText();

        // 采集期间每 5 秒从数据库拉一次最近记录
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _recordTimer.Tick += async (_, _) =>
        {
            if (IsAcquiring)
            {
                await RefreshRecordsAsync();
            }

            if (_triggerWatch is not null)
            {
                await RefreshTriggerRecordsAsync();
            }
        };
        _recordTimer.Start();

        _ = RefreshTablesAsync();
        _ = RefreshRecordsAsync();
        _ = ProbeSlavesAsync();   // 打开就探一遍从站，下拉框里直接可选
    }

    /// <summary>实时卡片与曲线，跟随点位表重建</summary>
    public ObservableCollection<TagViewModel> Tags { get; } = [];

    /// <summary>可编辑的采集点位表（直接就是配置对象里的集合）</summary>
    public ObservableCollection<PlcTag> TagConfigs { get; }

    /// <summary>扫描结果</summary>
    public ObservableCollection<ScanResult> ScanResults { get; } = [];

    /// <summary>库里已有的表</summary>
    public ObservableCollection<string> Tables { get; } = [];

    /// <summary>探测到的从站号，供工具栏下拉框选择</summary>
    public ObservableCollection<byte> SlaveIds { get; } = [];

    public IReadOnlyList<StorageModeOption> ModeOptions { get; }

    // ---------------- 连接 ----------------

    private bool CanConnect() => !IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect() => ConnectInternal();

    /// <summary>连接 PLC（切换从站后也走这里重连）</summary>
    private void ConnectInternal()
    {
        try
        {
            _modbus = new ModbusService(SlaveId);
            _modbus.Connect(Ip, Port);

            IsConnected = true;
            CommError = string.Empty;
            StatusText = $"已连接 {Ip}:{Port}（从站 {SlaveId}）";
            StartAcquisition();
            StartTriggerWatch();
        }
        catch (Exception ex)
        {
            _modbus?.Dispose();
            _modbus = null;
            IsConnected = false;
            StatusText = "连接失败";
            CommError = $"连接失败：{ex.Message}";
        }
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await StopAcquisitionAsync();
        await StopTriggerWatchAsync();
        _scanCts?.Cancel();
        _modbus?.Dispose();
        _modbus = null;

        IsConnected = false;
        StatusText = "已断开";
        foreach (var tag in Tags)
        {
            tag.MarkOffline();
        }
    }

    // ---------------- 从站探测与切换 ----------------

    [ObservableProperty] private string _slaveProbeStatus = "未探测";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeSlavesCommand))]
    private bool _isProbing;

    private bool CanProbeSlaves() => !IsProbing;

    /// <summary>探测同一 IP:端口 下有哪些从站号在响应，结果填进下拉框</summary>
    [RelayCommand(CanExecute = nameof(CanProbeSlaves))]
    private async Task ProbeSlavesAsync()
    {
        IsProbing = true;
        SlaveProbeStatus = $"探测中：从站 {ProbeStartId}~{ProbeEndId}，每个超时 {ProbeTimeoutMs} ms";

        try
        {
            var progress = new Progress<string>(message => SlaveProbeStatus = message);
            var results = await _probeService.ProbeAsync(
                Ip, Port, ProbeStartId, ProbeEndId, ProbeTimeoutMs, progress, CancellationToken.None);

            var found = results.Where(r => r.Responded).Select(r => r.UnitId).ToList();

            SlaveIds.Clear();
            foreach (var unitId in found)
            {
                SlaveIds.Add(unitId);
            }

            if (found.Count == 0)
            {
                var reason = results.FirstOrDefault(r => r.UnitId == ProbeStartId)?.Detail ?? "无响应";
                SlaveProbeStatus = $"未发现从站（从站 {ProbeStartId}：{reason}），检查 IP/端口";
                return;
            }

            SlaveProbeStatus = $"发现 {found.Count} 个从站：{string.Join("、", found)}";

            // 当前选中的从站不在列表里（或还没选），自动切到第一个可用从站
            if (!found.Contains(SlaveId))
            {
                SelectSlave(found[0]);
            }
        }
        catch (Exception ex)
        {
            SlaveProbeStatus = $"探测失败：{ex.Message}";
        }
        finally
        {
            IsProbing = false;
        }
    }

    /// <summary>切换从站号：已连接时自动断开重连，让新从站号生效</summary>
    private void SelectSlave(byte unitId)
    {
        SlaveId = unitId;
        SlaveIdText = unitId.ToString();
        SlaveProbeStatus = $"已切换到从站 {unitId}";
    }

    /// <summary>从站号文本变化：能解析成合法从站号才生效，否则只在状态栏提示，界面不标红</summary>
    partial void OnSlaveIdTextChanged(string value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (!byte.TryParse(text, out byte unitId) || unitId < 1 || unitId > 247)
        {
            if (text.Length > 0)
            {
                SlaveProbeStatus = $"从站号「{text}」不合法：应为 1~247 的整数";
            }

            return;
        }

        if (SlaveId != unitId)
        {
            SelectSlave(unitId);
        }
    }

    partial void OnSlaveIdChanged(byte value)
    {
        if (!IsConnected)
        {
            return;
        }

        _ = SwitchSlaveAsync();
    }

    /// <summary>从站号变了：重连并重启采集（ModbusService 的从站号是构造时固定的）</summary>
    private async Task SwitchSlaveAsync()
    {
        await StopAcquisitionAsync();
        await StopTriggerWatchAsync();
        _modbus?.Dispose();
        _modbus = null;
        IsConnected = false;

        ConnectInternal();
    }

    // ---------------- 采集控制 ----------------

    private bool CanStart() => IsConnected && !IsAcquiring;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => StartAcquisition();

    private bool CanStop() => IsAcquiring;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task Stop() => StopAcquisitionAsync();

    private void StartAcquisition()
    {
        if (_modbus is null || IsAcquiring || TagConfigs.Count == 0)
        {
            if (TagConfigs.Count == 0)
            {
                TagStatus = "点位表是空的，先去「采集点位」页扫描或添加点位";
            }

            return;
        }

        _acquisition = new PlcAcquisitionService(_modbus, [.. TagConfigs], BuildReadRanges())
        {
            IntervalMs = IntervalMs,
            PersistEnabled = PersistEnabled,
            PersistAsync = PersistBatchAsync
        };
        _acquisition.BatchCompleted += OnBatchCompleted;
        _acquisition.Failed += OnAcquisitionFailed;
        _acquisition.PersistFailed += OnPersistFailed;
        _acquisition.Start();

        IsAcquiring = true;
        StatusText = PersistEnabled
            ? $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {TagConfigs.Count} 个点位 → {_config.Groups.Count} 张表"
            : $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {TagConfigs.Count} 个点位，未落库";
    }

    private async Task StopAcquisitionAsync()
    {
        if (_acquisition is null)
        {
            return;
        }

        var service = _acquisition;
        _acquisition = null;
        service.BatchCompleted -= OnBatchCompleted;
        service.Failed -= OnAcquisitionFailed;
        service.PersistFailed -= OnPersistFailed;
        await service.StopAsync();

        IsAcquiring = false;
        StatusText = IsConnected ? "已连接（未采集）" : "已断开";
    }

    /// <summary>一轮采集结束：成功的点位刷新卡片，失败的点位只标灰自己，不影响其它点位</summary>
    private void OnBatchCompleted(AcquisitionBatch batch)
    {
        _dispatcher.BeginInvoke(() =>
        {
            foreach (var sample in batch.Samples)
            {
                var tag = Tags.FirstOrDefault(t => t.Tag.Address == sample.Tag.Address);
                tag?.Apply(sample);
            }

            foreach (var failure in batch.Failures)
            {
                var tag = Tags.FirstOrDefault(t => t.Tag.Address == failure.Tag.Address);
                tag?.MarkFailed(failure.Reason);
            }

            PersistError = string.Empty;
            CommError = batch.Failures.Count == 0 ? string.Empty : BuildFailureSummary(batch.Failures);

            string time = batch.Timestamp.ToString("HH:mm:ss");
            StatusText = batch.Failures.Count == 0
                ? $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · 最新一次 {time}"
                : $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {batch.Samples.Count}/{batch.Samples.Count + batch.Failures.Count} 个点位正常 · 最新一次 {time}";
        });
    }

    /// <summary>失败点位可能很多，只列前几个，避免状态栏被刷屏</summary>
    private static string BuildFailureSummary(IReadOnlyList<TagFailure> failures)
    {
        var preview = failures.Take(3).Select(f => $"{f.Tag.Name}({f.Tag.DisplayAddress})");
        string more = failures.Count > 3 ? $" 等 {failures.Count} 个点位" : string.Empty;
        return $"采集失败：{string.Join("、", preview)}{more} —— {failures[0].Reason}";
    }

    /// <summary>采集失败（通信层）：只上报错误并标灰，循环继续，界面不受影响</summary>
    private void OnAcquisitionFailed(string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            CommError = $"采集失败：{message}";
            foreach (var tag in Tags)
            {
                tag.MarkOffline();
            }
        });
    }

    /// <summary>落库失败：只提示，不影响实时监控</summary>
    private void OnPersistFailed(string message)
    {
        _dispatcher.BeginInvoke(() => PersistError = $"落库失败：{message}");
    }

    partial void OnIntervalMsChanged(int value)
    {
        if (_acquisition is not null)
        {
            _acquisition.IntervalMs = value;
            StatusText = $"采集中：{value} ms/次（采集周期已调整）";
        }
    }

    partial void OnPersistEnabledChanged(bool value)
    {
        if (_acquisition is not null)
        {
            _acquisition.PersistEnabled = value;
        }
    }

    /// <summary>
    /// 按存储组把一批数据拆开，各组并发写自己的目标表。
    /// 归组看地址落在哪个区段（用 _config.Groups 生效值，不看点位上那份展示用的缓存）；
    /// 界面上的组表是草稿，点【建表/校验结构】或【保存配置】后才生效，
    /// 免得边打字边把数据写到还不存在的表上。并发写是为了让慢的那张表不拖住其它组。
    /// </summary>
    private async Task PersistBatchAsync(IReadOnlyList<Sample> batch)
    {
        var jobs = new List<Task>();

        foreach (var group in _config.Groups)
        {
            if (group.Count == 0)
            {
                continue;
            }

            var samples = batch.Where(s => group.Covers(s.Tag.Address)).ToList();
            if (samples.Count == 0)
            {
                continue;
            }

            jobs.Add(group.Mode == StorageMode.Wide
                ? _tableStore.InsertWideAsync(group.TableName, samples)
                : _tableStore.InsertLongAsync(group.TableName, samples));
        }

        if (jobs.Count > 0)
        {
            await Task.WhenAll(jobs);
        }

        // 正常情况下采集只会读区段内的点位，走不到这里；真出现说明区段在采集中被改过，提醒一句
        int orphan = batch.Count(s => !_config.Groups.Any(g => g.Covers(s.Tag.Address)));
        if (orphan > 0)
        {
            throw new InvalidOperationException(
                $"有 {orphan} 个采样点不在任何存储组的区段内，没地方落库。去「存储映射」页调整区段范围。");
        }
    }

    // ---------------- 点位扫描 ----------------

    private bool CanScan() => IsConnected && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (_modbus is null)
        {
            return;
        }

        if (!TryParseAddress(ScanStart, out var start) || !TryParseAddress(ScanEnd, out var end))
        {
            ScanStatus = "地址格式不对，示例：40001（人读地址）或 0（协议地址）";
            return;
        }

        if (end < start)
        {
            ScanStatus = "结束地址不能小于起始地址";
            return;
        }

        var batchSize = (ushort)Math.Clamp(ScanBatchSize, 1, 120);
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanResults.Clear();
        ScanStatus = $"扫描中：{start} ~ {end}（协议地址），每段 {batchSize} 个";

        try
        {
            var progress = new Progress<string>(message => ScanStatus = message);
            var results = await _scanService.ScanAsync(_modbus, start, end, batchSize, progress, _scanCts.Token);

            foreach (var result in results)
            {
                ScanResults.Add(result);
            }

            int readable = results.Count(r => r.IsReadable);
            ScanStatus = $"扫描完成：共 {results.Count} 个地址，可读 {readable} 个，读异常 {results.Count - readable} 个";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "扫描已取消";
        }
        catch (Exception ex)
        {
            ScanStatus = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private bool CanCancelScan() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>把勾选的可读地址加入点位表</summary>
    [RelayCommand]
    private void AddSelectedScanResults()
    {
        var selected = ScanResults.Where(r => r.IsSelected && r.IsReadable).ToList();
        if (selected.Count == 0)
        {
            TagStatus = "没有勾选可读的地址";
            return;
        }

        int added = 0;
        foreach (var item in selected.Where(item => TagConfigs.All(t => t.Address != item.Address)))
        {
            TagConfigs.Add(new PlcTag(item.Address, $"点位{item.Address}", string.Empty, 1, 0, $"点位{item.Address}"));
            added++;
        }

        ApplyTagChanges();
        TagStatus = $"已加入 {added} 个点位，跳过重复 {selected.Count - added} 个。记得按需改名称与 Scale。";
    }

    [RelayCommand]
    private void SelectAllReadable()
    {
        foreach (var result in ScanResults)
        {
            result.IsSelected = result.IsReadable;
        }
    }

    /// <summary>批量移除点位：参数是 DataGrid 当前选中的行（支持框选 / Ctrl / Shift 多选）</summary>
    [RelayCommand]
    private void RemoveSelectedTag(IList? selectedRows)
    {
        // SelectedItems 是随界面选择实时变化的活集合，先快照再删，避免边遍历边被改
        var targets = selectedRows?.OfType<PlcTag>().ToList() ?? [];
        if (targets.Count == 0)
        {
            TagStatus = "没有选中点位";
            return;
        }

        foreach (var tag in targets)
        {
            TagConfigs.Remove(tag);
        }

        ApplyTagChanges();
        TagStatus = targets.Count == 1
            ? $"已删除点位「{targets[0].Name}」"
            : $"已删除 {targets.Count} 个点位";
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            CommitStorageMapping();
            TagStatus = $"配置已保存：{ConfigStore.FilePath}";
        }
        catch (Exception ex)
        {
            TagStatus = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>点位变了：重建卡片曲线、存盘，采集中则重启采集让新点位生效</summary>
    private void ApplyTagChanges()
    {
        RebuildTags();
        SaveConfig();

        if (IsAcquiring)
        {
            _ = RestartAcquisitionAsync();
        }
    }

    private async Task RestartAcquisitionAsync()
    {
        await StopAcquisitionAsync();
        StartAcquisition();
    }

    private void RebuildTags()
    {
        Tags.Clear();
        for (int i = 0; i < TagConfigs.Count; i++)
        {
            Tags.Add(new TagViewModel(TagConfigs[i], Palette[i % Palette.Length]));
        }
    }

    /// <summary>
    /// 兼容修复：早期版本改「名称」不会同步「宽表列名」，配置里会留下「名称=热封数量、列名=点位22」这种不一致。
    /// 列名若仍是扫描加入时的默认占位名（点位N），就按名称纠正过来；手工改过的列名不动。
    /// </summary>
    private static void ReconcileDefaultColumnNames(IEnumerable<PlcTag> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.ColumnName == $"点位{tag.Address}" && tag.Name != tag.ColumnName)
            {
                tag.ColumnName = tag.Name;
            }
        }
    }

    /// <summary>
    /// 点位表或任一点位的属性（名称/列名/换算）变了，建表语句预览都要跟着刷新，
    /// 否则会出现“界面里改了名称，预览和建表还是旧列名”的错觉。
    /// </summary>
    private void AttachTagSubscriptions()
    {
        TagConfigs.CollectionChanged += OnTagConfigsChanged;
        foreach (var tag in TagConfigs)
        {
            tag.PropertyChanged += OnTagPropertyChanged;
        }
    }

    private void OnTagConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (PlcTag tag in e.OldItems)
            {
                tag.PropertyChanged -= OnTagPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (PlcTag tag in e.NewItems)
            {
                tag.PropertyChanged += OnTagPropertyChanged;
            }
        }

        RefreshTagGroups();
        UpdateScriptPreview();
    }

    /// <summary>点位属性变了：地址会改变归组，名称/列名/换算会改变建表预览，都刷新一遍</summary>
    private void OnTagPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTagGroups();
        UpdateScriptPreview();
    }

    /// <summary>支持输入 40001 这样的人读地址，自动换算成 0-based 协议地址</summary>
    private static bool TryParseAddress(string? text, out ushort address)
    {
        address = 0;
        if (!int.TryParse(text?.Trim(), out int value) || value < 0 || value > 40001 + 65535)
        {
            return false;
        }

        if (value >= 40001)
        {
            value -= 40001;
        }

        if (value > 65535)
        {
            return false;
        }

        address = (ushort)value;
        return true;
    }

    // ---------------- 存储映射 ----------------

    /// <summary>组表草稿变了：补/退订阅，并刷新组名清单、点位归组与建表预览</summary>
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (StorageGroup group in e.OldItems)
            {
                group.PropertyChanged -= OnGroupPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (StorageGroup group in e.NewItems)
            {
                group.PropertyChanged += OnGroupPropertyChanged;
            }
        }

        RefreshGroupNames();
        RefreshTagGroups();
        UpdateScriptPreview();
    }

    /// <summary>组的区段/组名/表名任一改动，点位的归组和建表预览都要跟着变</summary>
    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshGroupNames();
        RefreshTagGroups();
        UpdateScriptPreview();
    }

    /// <summary>
    /// 重新推导每个点位归到哪个组：地址落在哪个区段就归哪个组。
    /// 区段是唯一判定依据，所以不存在「点位指向一个不存在的组」这种事。
    /// </summary>
    private void RefreshTagGroups()
    {
        // 改 Group 会反过来触发 OnTagPropertyChanged，这里挡一下重入
        if (_refreshingTagGroups)
        {
            return;
        }

        _refreshingTagGroups = true;
        try
        {
            foreach (var tag in TagConfigs)
            {
                tag.Group = ResolveGroupName(tag.Address);
            }
        }
        finally
        {
            _refreshingTagGroups = false;
        }
    }

    private bool _refreshingTagGroups;

    /// <summary>地址落在哪个区段；不在任何区段内返回空 —— 这种点位既不会采集也不会落库</summary>
    private string ResolveGroupName(ushort address)
    {
        foreach (var group in Groups)
        {
            if (group.Covers(address))
            {
                return group.Name;
            }
        }

        return string.Empty;
    }

    private void RefreshGroupNames()
    {
        GroupNames.Clear();
        foreach (var group in Groups)
        {
            GroupNames.Add(group.Name);
        }
    }

    /// <summary>交给采集循环的读取区段：只取真有点位的组，空区段读回来也没地方写</summary>
    private IReadOnlyList<ReadRange> BuildReadRanges() =>
        [.. _config.Groups
            .Where(g => g.Count > 0 && TagConfigs.Any(t => g.Covers(t.Address)))
            .OrderBy(g => g.StartAddress)
            .Select(g => new ReadRange(g.StartAddress, g.Count))];

    /// <summary>落在某个区段内的点位（区段是唯一的判定依据，不看点位上的归组缓存）</summary>
    private List<PlcTag> TagsOfGroup(StorageGroup group) =>
        [.. TagConfigs.Where(t => group.Covers(t.Address))];

    /// <summary>不在任何区段内的点位数：这些点位不会被采集、也不会落库</summary>
    private int CountUnassignedTags() => TagConfigs.Count(t => ResolveGroupName(t.Address).Length == 0);

    [RelayCommand]
    private void AddGroup()
    {
        int index = Groups.Count + 1;
        while (Groups.Any(g => string.Equals(g.Name, $"组{index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        // 新区段接在所有现有区段之后，默认 20 个寄存器（现场常见的一档）
        int start = Groups.Count == 0 ? 0 : Groups.Max(g => g.EndAddress) + 1;
        Groups.Add(new StorageGroup($"组{index}", $"PlcData{index}", StorageMode.Long, (ushort)start, 20));
        SelectedGroup = Groups[^1];
        StorageStatus = $"已新增组「组{index}」，区段 {Groups[^1].DisplayRange}：接着设目标表，并把区段改成本台设备实际的地址段";
    }

    [RelayCommand]
    private void RemoveGroup()
    {
        if (SelectedGroup is null)
        {
            StorageStatus = "先选中要删除的组";
            return;
        }

        if (Groups.Count <= 1)
        {
            StorageStatus = "至少要保留一个存储组";
            return;
        }

        var removedName = SelectedGroup.Name;
        int affected = TagsOfGroup(SelectedGroup).Count;
        Groups.Remove(SelectedGroup);
        SelectedGroup = Groups[0];

        StorageStatus = affected > 0
            ? $"已删除组「{removedName}」：原区段内的 {affected} 个点位现在显示为「未归组」，改成别的组的区段覆盖它们即可"
            : $"已删除组「{removedName}」";
    }

    /// <summary>
    /// 校验组表草稿：组名唯一非空、表名合法、一张表只归一个组、宽表别超列数上限。
    /// 顺手把两端的空格去掉，后面就不用到处 Trim。
    /// </summary>
    private void ValidateGroups()
    {
        if (Groups.Count == 0)
        {
            throw new InvalidOperationException("至少要有一个存储组。");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Groups)
        {
            group.Name = group.Name?.Trim() ?? string.Empty;
            group.TableName = group.TableName?.Trim() ?? string.Empty;

            if (group.Name.Length == 0)
            {
                throw new InvalidOperationException("组名不能为空。");
            }

            if (!names.Add(group.Name))
            {
                throw new InvalidOperationException($"组名「{group.Name}」重复：组名是区段的标识，必须唯一。");
            }

            if (group.Count == 0)
            {
                throw new InvalidOperationException($"组「{group.Name}」的区段长度是 0，至少要有 1 个寄存器。");
            }

            if (group.EndAddress > 65535)
            {
                throw new InvalidOperationException(
                    $"组「{group.Name}」的区段超出地址范围：起始 {group.StartAddress} + {group.Count} 个，" +
                    $"结束地址 {group.EndAddress} 超过 65535。");
            }

            DynamicTableStore.EnsureValidIdentifier(group.TableName, "表名");

            // 两个组写同一张表，列和点位会互相打架（宽表尤其明显），直接拦下
            if (tableOwners.TryGetValue(group.TableName, out var owner))
            {
                throw new InvalidOperationException($"表「{group.TableName}」同时被组「{owner}」和「{group.Name}」使用，请分开。");
            }

            tableOwners[group.TableName] = group.Name;

            int tagCount = TagsOfGroup(group).Count;
            if (group.Mode == StorageMode.Wide && tagCount > DynamicTableStore.MaxPointColumnsInWideTable)
            {
                throw new InvalidOperationException(
                    $"组「{group.Name}」是宽表但有 {tagCount} 个点位，超过单表列数上限" +
                    $"（{DynamicTableStore.MaxPointColumnsInWideTable} 个），请拆成多个组或改用长表。");
            }
        }

        // 区段重叠会让同一个地址同时属于两个组：采集读两遍、落库写两张表，直接拦下
        for (int i = 0; i < Groups.Count; i++)
        {
            for (int j = i + 1; j < Groups.Count; j++)
            {
                if (Groups[i].StartAddress <= Groups[j].EndAddress && Groups[j].StartAddress <= Groups[i].EndAddress)
                {
                    throw new InvalidOperationException(
                        $"组「{Groups[i].Name}」（{Groups[i].DisplayRange}）和组「{Groups[j].Name}」" +
                        $"（{Groups[j].DisplayRange}）的区段重叠了，同一个地址不能属于两个组。");
                }
            }
        }
    }

    /// <summary>把界面上的组表草稿提交成生效配置并落盘</summary>
    private void CommitStorageMapping()
    {
        _config.Groups.Clear();
        foreach (var group in Groups)
        {
            _config.Groups.Add(group.Clone());
        }

        UpdateEffectiveStorageText();
        ConfigStore.Save(_config);
    }

    private void UpdateEffectiveStorageText()
    {
        EffectiveStorageText = _config.Groups.Count == 0
            ? "当前没有存储组"
            : "当前写入：" + string.Join("　|　", _config.Groups.Select(g =>
                $"{g.Name} {g.DisplayRange} → {g.TableName}（{(g.Mode == StorageMode.Wide ? "宽表" : "长表")}）"));
    }

    /// <summary>建表语句预览：每个组一段，段首标出组名与点位数量</summary>
    private void UpdateScriptPreview()
    {
        try
        {
            var blocks = new List<string>();
            foreach (var group in Groups)
            {
                var tags = TagsOfGroup(group);
                blocks.Add($"-- 组「{group.Name}」→ 表 {group.TableName}（{tags.Count} 个点位）\r\n" +
                           DynamicTableStore.BuildCreateScript(group.TableName, group.Mode, tags));
            }

            CreateScriptPreview = blocks.Count > 0 ? string.Join("\r\n\r\n", blocks) : "（还没有存储组）";
        }
        catch (Exception ex)
        {
            CreateScriptPreview = $"（暂时无法生成建表语句：{ex.Message}）";
        }
    }

    [RelayCommand]
    private async Task RefreshTablesAsync()
    {
        try
        {
            var tables = await _tableStore.ListTablesAsync();
            Tables.Clear();
            foreach (var table in tables)
            {
                Tables.Add(table);
            }
        }
        catch (Exception ex)
        {
            StorageStatus = $"读取表清单失败：{ex.Message}";
        }
    }

    /// <summary>按组建表（已存在则补列）：先校验，全部建完才把组表提交生效</summary>
    [RelayCommand]
    private async Task CreateTableAsync()
    {
        try
        {
            StorageStatus = "处理中…";
            ValidateGroups();

            var notes = new List<string>();
            foreach (var group in Groups)
            {
                var tags = TagsOfGroup(group);
                bool created = await _tableStore.CreateTableIfMissingAsync(group.TableName, group.Mode, tags);
                var sync = group.Mode == StorageMode.Wide
                    ? await _tableStore.EnsureWideColumnsAsync(group.TableName, tags)
                    : new DynamicTableStore.WideColumnSyncResult([], [], []);

                notes.Add(DescribeGroupSync(group, tags.Count, created, sync));
            }

            CommitStorageMapping();
            _ = RefreshTablesAsync();

            StorageStatus = string.Join("；", notes);
            await RefreshRecordsAsync();
        }
        catch (Exception ex)
        {
            StorageStatus = $"建表失败：{ex.Message}";
        }
    }

    /// <summary>把一个组的建表结果压成一句话</summary>
    private static string DescribeGroupSync(
        StorageGroup group, int tagCount, bool created, DynamicTableStore.WideColumnSyncResult sync)
    {
        var note = new StringBuilder($"组「{group.Name}」");
        note.Append(created ? $"已创建表 {group.TableName}" : $"表 {group.TableName} 已存在，结构已校验");
        note.Append($"（{tagCount} 个点位）");

        if (sync.Added.Count > 0)
        {
            note.Append($"，补齐列：{string.Join("、", sync.Added)}");
        }

        if (sync.Relaxed.Count > 0)
        {
            note.Append($"，改为可空（允许个别点位读失败）：{string.Join("、", sync.Relaxed)}");
        }

        if (sync.BlockingNotNull.Count > 0)
        {
            note.Append($"；⚠ 表里这些非空列没人写，插入会失败，建议换新表名或把它们改成可空 / 删掉：" +
                        string.Join("、", sync.BlockingNotNull));
        }

        return note.ToString();
    }

    // ---------------- 触发记录（握手模拟上升沿） ----------------

    private void StartTriggerWatch()
    {
        if (_modbus is null || _triggerWatch is not null || !Triggers.Any(t => t.Enabled))
        {
            TriggerStatus = Triggers.Count == 0 ? "没有触发规则" : "触发规则全部停用";
            return;
        }

        _triggerWatch = new TriggerWatchService(_modbus, [.. Triggers])
        {
            PollIntervalMs = TriggerPollIntervalMs
        };
        _triggerWatch.Triggered += OnTriggered;
        _triggerWatch.HandshakeCompleted += OnHandshakeCompleted;
        _triggerWatch.Alarm += OnTriggerAlarm;
        _triggerWatch.Failed += OnAcquisitionFailed;
        _triggerWatch.Start();

        TriggerStatus = $"监听中：{Triggers.Count(t => t.Enabled)} 条规则 · {TriggerPollIntervalMs} ms 轮询";
        _ = EnsureTriggerTableAsync();
    }

    private async Task StopTriggerWatchAsync()
    {
        var service = _triggerWatch;
        if (service is null)
        {
            return;
        }

        _triggerWatch = null;
        service.Triggered -= OnTriggered;
        service.HandshakeCompleted -= OnHandshakeCompleted;
        service.Alarm -= OnTriggerAlarm;
        service.Failed -= OnAcquisitionFailed;
        await service.StopAsync();
        service.Dispose();

        _stuckRules.Clear();
        TriggerAlarm = string.Empty;
        TriggerStatus = "未监听";
    }

    /// <summary>规则增删后要重启监听：监听循环用的是启动那一刻的规则快照</summary>
    private async Task RestartTriggerWatchAsync()
    {
        if (_triggerWatch is null)
        {
            return;
        }

        await StopTriggerWatchAsync();
        StartTriggerWatch();
    }

    private async Task EnsureTriggerTableAsync()
    {
        try
        {
            await _triggerStore.EnsureTableAsync(TriggerLogStore.DefaultTableName);
            await RefreshTriggerRecordsAsync();
        }
        catch (Exception ex)
        {
            _ = _dispatcher.BeginInvoke(() => TriggerAlarm = $"触发记录表准备失败：{ex.Message}");
        }
    }

    /// <summary>该记一条记录了：先刷界面，落库丢到后台，别拖慢 100ms 的轮询</summary>
    private void OnTriggered(TriggerRule rule, DateTime timestamp)
    {
        _dispatcher.BeginInvoke(() =>
        {
            TriggerCount++;
            TriggerStatus = $"监听中：{Triggers.Count(t => t.Enabled)} 条规则 · 累计 {TriggerCount} 次 · " +
                            $"最近「{rule.Name}」@ {timestamp:HH:mm:ss}";
        });

        _ = PersistTriggerAsync(rule, timestamp);
    }

    private async Task PersistTriggerAsync(TriggerRule rule, DateTime timestamp)
    {
        try
        {
            await _triggerStore.InsertAsync(TriggerLogStore.DefaultTableName, rule, timestamp);
        }
        catch (Exception ex)
        {
            _ = _dispatcher.BeginInvoke(() => TriggerAlarm = $"触发记录落库失败：{ex.Message}");
        }
    }

    private void OnHandshakeCompleted(TriggerRule rule)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _stuckRules.Remove(rule.Name);
            UpdateTriggerAlarmText();
        });
    }

    private void OnTriggerAlarm(TriggerRule rule, string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _stuckRules.Add(rule.Name);
            UpdateTriggerAlarmText();
            TriggerStatus = message;      // 详细原因放状态栏，卡在哪条规则的「状态」列也能看到
        });
    }

    /// <summary>报警条只列"哪几条规则卡住"，详细原因在状态栏与规则表的「状态」列</summary>
    private void UpdateTriggerAlarmText()
    {
        TriggerAlarm = _stuckRules.Count == 0
            ? string.Empty
            : $"握手卡住：{string.Join("、", _stuckRules)} —— 等 PLC 清信号，未强行复位";
    }

    partial void OnTriggerPollIntervalMsChanged(int value)
    {
        if (_triggerWatch is not null)
        {
            _triggerWatch.PollIntervalMs = value;
            TriggerStatus = $"监听中：轮询间隔已改为 {value} ms";
        }
    }

    [RelayCommand]
    private async Task AddTriggerRuleAsync()
    {
        int index = Triggers.Count + 1;
        while (Triggers.Any(t => string.Equals(t.Name, $"触发{index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        var rule = new TriggerRule($"触发{index}", 0, 0);
        Triggers.Add(rule);
        SelectedTrigger = rule;
        ConfigStore.Save(_config);
        await RestartTriggerWatchAsync();

        TriggerStatus = $"已新增规则「{rule.Name}」：把信号/应答地址改成现场实际地址（0 基协议地址）";
    }

    [RelayCommand]
    private async Task RemoveTriggerRuleAsync(IList? selectedRows)
    {
        // SelectedItems 是活集合，先快照再删
        var targets = selectedRows?.OfType<TriggerRule>().ToList() ?? [];
        if (targets.Count == 0)
        {
            TriggerStatus = "没有选中规则";
            return;
        }

        foreach (var rule in targets)
        {
            Triggers.Remove(rule);
        }

        ConfigStore.Save(_config);
        await RestartTriggerWatchAsync();
        TriggerStatus = $"已删除 {targets.Count} 条触发规则";
    }

    [RelayCommand]
    private void SaveTriggerRules()
    {
        try
        {
            ConfigStore.Save(_config);
            TriggerStatus = $"触发规则已保存：{ConfigStore.FilePath}";
        }
        catch (Exception ex)
        {
            TriggerStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshTriggerRecordsAsync()
    {
        try
        {
            var data = await _triggerStore.QueryRecentAsync(TriggerLogStore.DefaultTableName, RecentRecordCount);
            TriggerView = data.DefaultView;
            TriggerRecordsInfo =
                $"最近 {data.Rows.Count} 条 · 表 {TriggerLogStore.DefaultTableName} · 更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            TriggerRecordsInfo = $"读取触发记录失败：{ex.Message}";
        }
    }

    // ---------------- 记录查询 ----------------

    [RelayCommand]
    private async Task RefreshRecordsAsync()
    {
        // 预览选中的那个组；没选就预览第一个
        var target = SelectedGroup ?? Groups.FirstOrDefault();
        if (target is null)
        {
            RecentView = null;
            RecordsInfo = "还没有存储组";
            return;
        }

        try
        {
            var data = await _tableStore.QueryRecentAsync(target.TableName.Trim(), RecentRecordCount);
            RecentView = data.DefaultView;
            RecordsInfo = $"组「{target.Name}」· 表 {target.TableName} · 最近 {data.Rows.Count} 条 · 更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            RecordsInfo = $"读取数据库失败：{ex.Message}";
        }
    }

    public void Dispose()
    {
        _recordTimer.Stop();
        _scanCts?.Cancel();
        _acquisition?.Dispose();
        _triggerWatch?.Dispose();
        _modbus?.Dispose();
    }
}
