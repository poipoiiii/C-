using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcDataCollector.Services;
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

    private ModbusService? _modbus;
    private PlcAcquisitionService? _acquisition;
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
    [ObservableProperty] private PlcTag? _selectedTag;

    // ---------------- 存储映射 ----------------

    [ObservableProperty] private string _tableName = "DataRecords";
    [ObservableProperty] private StorageMode _selectedMode = StorageMode.Long;
    [ObservableProperty] private string _storageStatus = "";
    [ObservableProperty] private string _createScriptPreview = "";
    [ObservableProperty] private string _effectiveStorageText = "";
    [ObservableProperty] private DataView? _recentView;

    public MainViewModel()
    {
        _config = ConfigStore.Load();
        ReconcileDefaultColumnNames(_config.Tags);
        TagConfigs = _config.Tags;
        TableName = _config.Storage.TableName;
        SelectedMode = _config.Storage.Mode;

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

        _acquisition = new PlcAcquisitionService(_modbus, [.. TagConfigs])
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
            ? $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {TagConfigs.Count} 个点位 → {_config.Storage.TableName}"
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
    /// 按存储映射把一批数据写到目标表。
    /// 注意用的是 _config.Storage（生效值），界面上的表名/模式只是草稿，点“建表/校验结构”或“保存配置”后才生效，
    /// 免得边打字边把数据写到还不存在的表上。
    /// </summary>
    private async Task PersistBatchAsync(IReadOnlyList<Sample> batch)
    {
        if (_config.Storage.Mode == StorageMode.Wide)
        {
            await _tableStore.InsertWideAsync(_config.Storage.TableName, batch);
        }
        else
        {
            await _tableStore.InsertLongAsync(_config.Storage.TableName, batch);
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

    [RelayCommand]
    private void RemoveSelectedTag()
    {
        if (SelectedTag is null)
        {
            return;
        }

        var name = SelectedTag.Name;
        TagConfigs.Remove(SelectedTag);
        ApplyTagChanges();
        TagStatus = $"已删除点位「{name}」";
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

        UpdateScriptPreview();
    }

    private void OnTagPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateScriptPreview();

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

    partial void OnTableNameChanged(string value) => UpdateScriptPreview();

    partial void OnSelectedModeChanged(StorageMode value) => UpdateScriptPreview();

    /// <summary>把界面上的草稿值提交成生效值并落盘</summary>
    private void CommitStorageMapping()
    {
        _config.Storage.TableName = TableName.Trim();
        _config.Storage.Mode = SelectedMode;
        UpdateEffectiveStorageText();
        ConfigStore.Save(_config);
    }

    private void UpdateEffectiveStorageText()
    {
        var modeText = _config.Storage.Mode == StorageMode.Wide ? "宽表" : "长表";
        EffectiveStorageText = $"当前写入：{_config.Storage.TableName}（{modeText}）";
    }

    private void UpdateScriptPreview()
    {
        try
        {
            CreateScriptPreview = DynamicTableStore.BuildCreateScript(TableName, SelectedMode, TagConfigs);
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

    /// <summary>建表（已存在则补列）：先按界面草稿建表，成功后才把映射提交生效</summary>
    [RelayCommand]
    private async Task CreateTableAsync()
    {
        try
        {
            StorageStatus = "处理中…";

            bool created = await _tableStore.CreateTableIfMissingAsync(TableName, SelectedMode, TagConfigs);
            var sync = SelectedMode == StorageMode.Wide
                ? await _tableStore.EnsureWideColumnsAsync(TableName, TagConfigs)
                : new DynamicTableStore.WideColumnSyncResult([], [], []);

            CommitStorageMapping();
            _ = RefreshTablesAsync();

            StorageStatus = created ? $"已创建表 {TableName}" : $"表 {TableName} 已存在，结构已校验";
            if (sync.Added.Count > 0)
            {
                StorageStatus += $"；补齐列：{string.Join("、", sync.Added)}";
            }

            if (sync.Relaxed.Count > 0)
            {
                StorageStatus += $"；改为可空（允许个别点位读失败）：{string.Join("、", sync.Relaxed)}";
            }

            if (sync.BlockingNotNull.Count > 0)
            {
                StorageStatus += $"；⚠ 表里这些非空列没人写，插入会失败，建议换新表名或把它们改成可空 / 删掉：" +
                                 string.Join("、", sync.BlockingNotNull);
            }

            await RefreshRecordsAsync();
        }
        catch (Exception ex)
        {
            StorageStatus = $"建表失败：{ex.Message}";
        }
    }

    // ---------------- 记录查询 ----------------

    [RelayCommand]
    private async Task RefreshRecordsAsync()
    {
        try
        {
            var data = await _tableStore.QueryRecentAsync(_config.Storage.TableName, RecentRecordCount);
            RecentView = data.DefaultView;
            RecordsInfo = $"最近 {data.Rows.Count} 条 · 表 {_config.Storage.TableName} · 更新于 {DateTime.Now:HH:mm:ss}";
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
        _modbus?.Dispose();
    }
}
