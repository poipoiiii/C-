using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcCore.Services;
using PlcCore.Models;
using PlcHmi.Services;
using Serilog;

namespace PlcHmi.ViewModels;

/// <summary>
/// 外壳视图模型：只管**跨页签**的东西 —— 机型配置、连接、采集生命周期，以及状态栏。
/// 四个页签各有自己的视图模型（<see cref="Dashboard"/> / <see cref="Scan"/> / <see cref="Storage"/> / <see cref="Trigger"/>），
/// 它们通过这里的 <see cref="Config"/>、<see cref="Modbus"/> 拿共享状态，并把"点位变了""要保存"这类请求交回来。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>从站探测范围（协议里 UnitId 是 0~247，0 一般作广播，现场多从 1 开始且很少超过 32）</summary>
    private const byte ProbeStartId = 1;
    private const byte ProbeEndId = 32;

    /// <summary>单个从站的探测超时：短一点，避免探测一轮等太久</summary>
    private const int ProbeTimeoutMs = 400;

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly AppServices _services;

    /// <summary>本机档案（data/machine.json）：机台号 + 连接参数 + 当前机型。机型配置里不再放这些</summary>
    private MachineProfile _machine;

    /// <summary>机型模板缺失的告警（安装包不完整），启动时提示一次</summary>
    private string? _templateWarning;

    /// <summary>当前机型的配置；切换机型时整份换掉</summary>
    private PlcConfig _config;

    /// <summary>当前机型的配置读写（一个实例对应一个机型）</summary>
    private ConfigStore _store;

    /// <summary>连接控制器：Modbus 链路的生命周期与连接参数（Ip/Port/SlaveId/IntervalMs）</summary>
    private readonly ConnectionController _connection;

    /// <summary>采集控制器：采集循环的生命周期与落库</summary>
    private readonly AcquisitionController _acquisition;

    private TriggerWatchService? _triggerWatch;

    public MainViewModel() : this(AppServices.Default)
    {
    }

    public MainViewModel(AppServices services)
    {
        _services = services;

        // 机型：命令行 --config / --machine > 本机档案（机台号、上次选的机型）> 已有配置 > 已知机型第一个
        _machine = MachineStore.Load();

        // 主题跟着本机档案走，先把配色装好；直接写字段，启动不该触发一次落盘
        _isDarkTheme = string.Equals(_machine.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        AppTheme.Apply(_isDarkTheme);

        string profile = ProfileResolver.Resolve(Environment.GetCommandLineArgs(), _machine);
        _config = LoadConfig(profile, out _store);
        MigrateMachineProfile();                     // 老配置里的机台号/连接参数搬进本机档案

        // 迁移可能刚把机台号捡回来：再判一次机型（老档案叫「默认」，而机台号说明它其实是 AIXP）
        string resolved = ProfileResolver.Resolve(Environment.GetCommandLineArgs(), _machine);
        if (resolved != profile)
        {
            // 老档案是这台设备现场调好的真实配置：新机型的档案还没有时，直接拿它当初始内容，
            // 别用模板把点位表盖掉（老档案本身留着不动，下拉里还能选回去）
            if (!File.Exists(ConfigStore.PathFor(resolved)))
            {
                ConfigStore.CopyConfigFile(_store.Profile, resolved);
            }

            profile = resolved;
            _config = LoadConfig(profile, out _store);
        }

        _machineNo = _machine.MachineNo;             // 直接写字段：启动不该触发一次保存

        if (_machine.Profile != profile)
        {
            _machine.Profile = profile;
            SaveMachineQuietly();
        }

        // 连接与采集各一个控制器：链路生命周期 / 采集循环与落库都收在 PlcCore 里。
        // 控制器不碰界面，状态用事件报回来；面向操作工的文案与日志留在这里
        _connection = new ConnectionController(services, _machine);
        _acquisition = new AcquisitionController(services, () => Config);
        _connection.Connected += OnConnected;
        _connection.ConnectFailed += OnConnectFailed;
        _connection.Disconnected += OnDisconnected;
        _connection.PersistFailed += OnConnectionPersistFailed;
        _acquisition.BatchCompleted += OnBatchCompleted;
        _acquisition.Failed += OnAcquisitionFailed;
        _acquisition.PersistFailed += OnPersistFailed;

        // 四个页签各一个视图模型；它们都拿外壳当共享上下文
        Storage = new StorageViewModel(this, services);
        Dashboard = new DashboardViewModel(this, services);
        Scan = new ScanViewModel(this, services);
        Trigger = new TriggerViewModel(this, services);

        Dashboard.Rebuild();
        ApplyConnectionConfig();
        RefreshProfileStatus();
        if (_store.LoadWarning is { } warning)
        {
            StatusText = warning;
            Log.Warning("配置告警：{Warning}", warning);
        }
        else if (_templateWarning is { } templateWarning)
        {
            StatusText = templateWarning;
            Log.Warning("{Warning}", templateWarning);
        }

        Log.Information("已启动：机台号 {MachineNo} · 机型 {Profile}（{ConfigFile}）",
            NeedsMachineNo ? "未填" : MachineNo, _store.Profile, _store.FilePath);

        _ = Storage.RefreshTablesAsync();
        _ = Dashboard.RefreshRecordsAsync();
    }

    // ---------------- 共享上下文（给四个页签用） ----------------

    /// <summary>当前机型的配置：点位表、存储组、触发规则都在里面</summary>
    public PlcConfig Config => _config;

    /// <summary>当前机型的配置读写</summary>
    public ConfigStore Store => _store;

    /// <summary>已连接的 Modbus 客户端；没连接时为 null</summary>
    public IModbusClient? Modbus => _connection.Modbus;

    /// <summary>触发监听；没在监听时为 null（轮询间隔改这个对象的属性即可生效）</summary>
    public TriggerWatchService? TriggerWatch => _triggerWatch;

    public DashboardViewModel Dashboard { get; }

    public ScanViewModel Scan { get; }

    public StorageViewModel Storage { get; }

    public TriggerViewModel Trigger { get; }

    // ---------------- 本机档案与机型 ----------------

    /// <summary>暗色主题：勾上就换成暗色皮肤，选择记在本机档案里，下次开机照旧</summary>
    [ObservableProperty] private bool _isDarkTheme;

    partial void OnIsDarkThemeChanged(bool value)
    {
        AppTheme.Apply(value);

        // 曲线坐标轴的颜色取自主题，卡片得按新配色重建一遍
        Dashboard.Rebuild();

        _machine.Theme = value ? "Dark" : "Light";
        SaveMachineQuietly();
    }

    /// <summary>
    /// 机台号：现场这台设备的编号（4 字母 + 3 数字，前 4 位决定机型）。
    /// 进项目时若为空，界面会弹框让用户填；**改它要输密码**（见 MainWindow 的 EditMachineNo_Click），
    /// 免得现场随手一改顺手把机型也换了。小写会自动补成大写。
    /// </summary>
    [ObservableProperty] private string _machineNo = string.Empty;

    /// <summary>状态栏那行；机型已经大字展示在左上角，这里只留配置文件路径，不重复</summary>
    [ObservableProperty] private string _profileStatus = string.Empty;

    /// <summary>还没填机台号：界面据此决定要不要弹输入框</summary>
    public bool NeedsMachineNo => string.IsNullOrWhiteSpace(MachineNo);

    /// <summary>
    /// 机台号还没填对（空着，或不是五个机型之一）：进项目时据此决定要不要弹输入框。
    /// 正常路径下界面是硬校验的，只有手工改过的 machine.json 才可能带着一个认不出的号码进来。
    /// </summary>
    public bool NeedsMachineNoFix => !MachineModels.TryResolve(MachineNo, out _);

    /// <summary>左上角的大字：机台号</summary>
    public string MachineNoText => NeedsMachineNo ? "未填写" : MachineNo;

    /// <summary>机台号有问题（没填 / 格式不对 / 代号不在表里）：界面标黄</summary>
    public bool HasMachineNoProblem => MachineNoHint.Length > 0;

    /// <summary>机台号的问题说明；没问题时为空串</summary>
    public string MachineNoHint
    {
        get
        {
            if (NeedsMachineNo)
            {
                return "还没填机台号：点左上角大字填一下，前 4 位决定机型";
            }

            // 机台号是硬校验的（只能是五个机型之一），走到这里的只可能是手工改过的 machine.json
            return MachineModels.TryResolve(MachineNo, out _) ? string.Empty : MachineModels.RequirementHint;
        }
    }

    /// <summary>机台号下面那行小字：这个机台号对应的机型</summary>
    public string MachineModelText
    {
        get
        {
            string model = MachineModels.Find(_store.Profile) is { } found
                ? $"{found.Name}（{found.Code}）"
                : $"{_store.Profile}（自定义档案，不在已知机型表里）";

            return HasMachineNoProblem ? $"{model} · {MachineNoHint}" : model;
        }
    }

    /// <summary>机台号大字的悬停说明：机型信息挪到提示里，界面上只留大字</summary>
    public string MachineNoTooltip => $"{MachineModelText}\n点击大字即可修改（改动需要密码）";

    /// <summary>
    /// 机台号改了：小写补成大写、存进本机档案，并按前 4 位切到对应机型（AIXP999 → AIXP）。
    /// 界面是硬校验的（只能是五个机型之一），所以这里认不出机型的分支只是兜底：
    /// 手工改过 machine.json 才会走到，那时不切机型、维持当前机型继续跑。
    /// </summary>
    partial void OnMachineNoChanged(string value)
    {
        string machineNo = MachineModels.Normalize(value);

        if (!string.Equals(machineNo, value, StringComparison.Ordinal))
        {
            MachineNo = machineNo;   // 回写大写：这一遍值就相同了，会再进来一次然后往下走
            return;
        }

        _machine.MachineNo = machineNo;
        NotifyMachineHeader();

        if (!MachineModels.TryResolve(machineNo, out var model))
        {
            SaveMachineQuietly();
            return;
        }

        if (model.Code == _store.Profile)
        {
            SaveMachineQuietly();
            StatusText = $"机台号 {machineNo} → 机型 {model.Display}";
            return;
        }

        _machine.Profile = model.Code;
        SaveMachineQuietly();
        _ = SwitchProfileAsync(model.Code);
    }

    /// <summary>机台号那几块（大字 / 机型说明 / 标黄状态）一起刷新</summary>
    private void NotifyMachineHeader()
    {
        OnPropertyChanged(nameof(MachineNoText));
        OnPropertyChanged(nameof(MachineModelText));
        OnPropertyChanged(nameof(MachineNoTooltip));
        OnPropertyChanged(nameof(MachineNoHint));
        OnPropertyChanged(nameof(HasMachineNoProblem));
        OnPropertyChanged(nameof(NeedsMachineNo));
    }

    /// <summary>状态栏那行只写配置文件路径（机型在左上角，不重复）</summary>
    private void RefreshProfileStatus() => ProfileStatus = $"配置文件：{_store.FilePath}";

    /// <summary>本机档案落盘；失败只在状态栏提示，不打断操作</summary>
    private void SaveMachineQuietly()
    {
        try
        {
            MachineStore.Save(_machine);
        }
        catch (Exception ex)
        {
            StatusText = $"本机档案保存失败：{ex.Message}";
            Log.Error(ex, "本机档案保存失败：{File}", AppPaths.MachineFile);
        }
    }

    /// <summary>
    /// 老配置把机台号与连接参数存在机型配置里（那时"一台机器一份配置"），现在搬到 data/machine.json：
    /// 机器级的东西跟着设备走，机型配置才能做到同型号完全一致、整份复制给另一台机器用。
    /// </summary>
    private void MigrateMachineProfile()
    {
        bool machineChanged = false;

        if (string.IsNullOrWhiteSpace(_machine.MachineNo) && !string.IsNullOrWhiteSpace(_config.LegacyMachineNo))
        {
            _machine.MachineNo = _config.LegacyMachineNo.Trim();
            machineChanged = true;
        }

        if (_config.LegacyConnection is { } legacy)
        {
            _machine.Ip = legacy.Ip;
            _machine.Port = legacy.Port;
            _machine.SlaveId = legacy.SlaveId;
            _machine.IntervalMs = legacy.IntervalMs;
            machineChanged = true;
        }

        if (_config.LegacyMachineNo is null && _config.LegacyConnection is null)
        {
            if (machineChanged)
            {
                SaveMachineQuietly();
            }

            return;
        }

        // 老字段清掉：机器级信息以后只写在 machine.json 里
        _config.LegacyMachineNo = null;
        _config.LegacyConnection = null;
        SaveConfigNow();
        SaveMachineQuietly();

        Log.Information("已把机台号/连接参数从机型配置搬到 {File}", AppPaths.MachineFile);
    }

    /// <summary>
    /// 打开一个机型的配置：现场副本不在就从随包发布的机型模板复制一份
    /// （第一次上这个型号、或者换到没见过的机型时就是这么建档案的）；
    /// 模板也缺就按内置默认点位建，并记一条告警（安装包不完整）。
    /// </summary>
    private PlcConfig LoadConfig(string profile, out ConfigStore store)
    {
        store = ConfigStore.Open(profile);

        if (!ConfigStore.EnsureConfigFile(profile))
        {
            _templateWarning = $"没找到机型模板 Templates/{profile}.json（安装包可能不完整）：" +
                               $"已按内置默认点位建了 {profile}.json，请核对点位表";
        }

        var config = store.Load();
        ReconcileDefaultColumnNames(config.Tags);
        return config;
    }

    /// <summary>
    /// 切换机型：先断开连接，再整份换配置。机台号与连接参数属于本机档案，不跟着机型走。
    /// </summary>
    private async Task SwitchProfileAsync(string profile)
    {
        try
        {
            if (IsConnected)
            {
                await DisconnectAsync();
            }

            Scan.CancelIfScanning();

            // 先把新机型读出来，读失败就整个放弃、继续用原机型（_store 保持不动）
            var nextConfig = LoadConfig(profile, out var nextStore);
            _store = nextStore;
            _config = nextConfig;

            // 四个页签各自按新配置重建（点位表、组表草稿、规则、卡片）
            Scan.OnConfigChanged();
            Trigger.OnConfigChanged();
            Storage.RebuildFromConfig();
            Dashboard.Rebuild();

            RefreshProfileStatus();
            NotifyMachineHeader();

            StatusText = _store.LoadWarning ??
                $"已切换到机型「{profile}」（{MachineModels.Find(profile)?.Name ?? "自定义"}）：" +
                $"{_config.Tags.Count} 个点位 · {_config.Groups.Count} 个存储组";
            Log.Information("已切换机型：{Profile}（{ConfigFile}）", profile, _store.FilePath);
        }
        catch (Exception ex)
        {
            StatusText = $"切换机型失败：{ex.Message}";
            Log.Error(ex, "切换机型失败：{Profile}", profile);
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

    /// <summary>把当前配置落盘；失败会抛，由调用方决定怎么提示</summary>
    public void SaveConfigNow() => _store.Save(_config);

    /// <summary>点位变了：重建卡片、存盘，采集中则重启采集让新点位生效</summary>
    public void OnTagsChanged()
    {
        Dashboard.Rebuild();

        try
        {
            SaveConfigNow();
        }
        catch (Exception ex)
        {
            StatusText = $"配置保存失败：{ex.Message}";
            Log.Error(ex, "配置保存失败：{ConfigFile}", _store.FilePath);
        }

        if (IsAcquiring)
        {
            _ = RestartAcquisitionAsync();
        }
    }

    // ---------------- 连接与采集 ----------------

    [ObservableProperty] private string _ip = "127.0.0.1";
    [ObservableProperty] private int _port = 5020;
    [ObservableProperty] private byte _slaveId = 1;

    /// <summary>顶栏「PLC 通信」分区的摘要；参数在配置弹窗里一次填完，这里只展示</summary>
    public string PlcConfigSummary => $"{Ip}:{Port} · 从站 {SlaveId} · {IntervalMs} ms/次";

    private void NotifyPlcConfigSummary() => OnPropertyChanged(nameof(PlcConfigSummary));

    /// <summary>
    /// 把界面上这几个可观察属性同步给连接控制器：参数由控制器持有（连接、探测、写本机档案都用它那一份），
    /// 界面绑定的仍是这里的属性，改哪个都从这几处收口，两边不会走岔。
    /// </summary>
    private void SyncConnectionConfig()
    {
        NotifyPlcConfigSummary();
        _connection.Ip = Ip;
        _connection.Port = Port;
        _connection.SlaveId = SlaveId;
        _connection.IntervalMs = IntervalMs;
    }

    partial void OnIpChanged(string value) => SyncConnectionConfig();

    partial void OnPortChanged(int value) => SyncConnectionConfig();

    /// <summary>
    /// 从站号输入框绑的是文本，不是 byte：直接绑 byte 的话边打字边转换，
    /// 中间态（比如只打了半个数字）会弹红色的“未能转换值”校验错误。
    /// </summary>
    [ObservableProperty] private string _slaveIdText = "1";
    [ObservableProperty] private int _intervalMs = 1000;
    [ObservableProperty] private bool _persistEnabled = true;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isAcquiring;

    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private string _commError = string.Empty;
    [ObservableProperty] private string _persistError = string.Empty;

    /// <summary>有通信/连接错误：顶栏状态那行据此标红提醒</summary>
    public bool HasCommError => CommError.Length > 0;

    partial void OnCommErrorChanged(string value) => OnPropertyChanged(nameof(HasCommError));

    /// <summary>
    /// 工具条上就两个开关，勾选状态即系统状态（勾上=已连接 / 采集中），按钮文字写的是"点下去会发生什么"（连接↔断开、开始采集↔停止采集）。
    /// 用户点击 → 走下面的动作；状态回写（连接失败、切换机型等）→ 经 <see cref="_syncingSwitches"/> 只改显示，不再触发动作。
    /// </summary>
    [ObservableProperty] private bool _connectionSwitch;

    [ObservableProperty] private bool _acquisitionSwitch;

    /// <summary>正在由状态回写开关：挡住开关的变更处理器，免得连接/断开动作被反过来又触发一遍</summary>
    private bool _syncingSwitches;

    partial void OnConnectionSwitchChanged(bool value)
    {
        if (_syncingSwitches)
        {
            return;
        }

        if (value)
        {
            _connection.Connect();
        }
        else
        {
            _ = DisconnectAsync();
        }
    }

    partial void OnAcquisitionSwitchChanged(bool value)
    {
        if (_syncingSwitches)
        {
            return;
        }

        if (value)
        {
            StartAcquisition();
        }
        else
        {
            _ = StopAcquisitionAsync();
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        SyncSwitches();
        NotifyControlAvailability();
    }

    partial void OnIsAcquiringChanged(bool value)
    {
        SyncSwitches();
        NotifyControlAvailability();
    }

    /// <summary>把两个开关拨到与当前状态一致（动作没生效时靠它把开关还原）</summary>
    private void SyncSwitches()
    {
        if (_syncingSwitches)
        {
            return;
        }

        _syncingSwitches = true;
        try
        {
            ConnectionSwitch = IsConnected;
            AcquisitionSwitch = IsAcquiring;
        }
        finally
        {
            _syncingSwitches = false;
        }
    }

    /// <summary>连上了（含换从站后的重连）：状态栏与日志在这里收口</summary>
    private void OnConnected()
    {
        IsConnected = true;
        CommError = string.Empty;
        StatusText = $"已连接 {Ip}:{Port}（从站 {SlaveId}）· 未采集";
        Log.Information("已连接 PLC：{Ip}:{Port}（从站 {SlaveId}）", Ip, Port, SlaveId);
    }

    /// <summary>连接失败：链路已经清掉，这里只提示并记日志</summary>
    private void OnConnectFailed(Exception ex)
    {
        IsConnected = false;
        StatusText = "连接失败";
        CommError = $"连接失败：{ex.Message}";
        Log.Error(ex, "连接 PLC 失败：{Ip}:{Port}（从站 {SlaveId}）", Ip, Port, SlaveId);

        // 连不上时 IsConnected 没变过，不会触发状态回写，开关得手动拨回来
        SyncSwitches();
    }

    /// <summary>连接参数落盘失败：只在状态栏提示，不打断连接</summary>
    private void OnConnectionPersistFailed(Exception ex)
    {
        StatusText = $"本机档案保存失败：{ex.Message}";
        Log.Error(ex, "本机档案保存失败：{File}", AppPaths.MachineFile);
    }

    /// <summary>把本机档案里的连接参数填回界面（启动时用；切机型不动它们）。同步给连接控制器由属性变更处理器负责</summary>
    private void ApplyConnectionConfig()
    {
        Ip = _machine.Ip;
        Port = _machine.Port;
        IntervalMs = _machine.IntervalMs;

        // 先赋 SlaveId（未连接时它的变更处理器直接返回），再赋文本：
        // 反过来的话会经由 SelectSlave 弹一句“已切换到从站 N”
        SlaveId = _machine.SlaveId;
        SlaveIdText = _machine.SlaveId.ToString();
    }

    /// <summary>断开：停采集（连带触发监听）+ 断链路</summary>
    private async Task DisconnectAsync()
    {
        await StopAcquisitionAsync();
        Scan.CancelIfScanning();
        _connection.Disconnect();
    }

    /// <summary>链路断开了：状态栏、卡片标灰与日志在这里收口</summary>
    private void OnDisconnected()
    {
        IsConnected = false;
        StatusText = "已断开";
        Dashboard.MarkAllOffline();
        Log.Information("已断开 PLC 连接");
    }

    // ---------------- 从站探测与切换 ----------------

    [ObservableProperty] private string _slaveProbeStatus = "未探测";

    /// <summary>探测/从站号有问题：配置弹窗里那行提示据此标红</summary>
    [ObservableProperty] private bool _slaveProbeFailed;

    /// <summary>
    /// 从站是否配置成功：从站号是 1~247 的整数，且最近一次探测没判失败。
    /// 没配好之前【连接】【开始采集】【写入数据库】一律点不动 —— 拿一个错的从站号去连、去写库，
    /// 轻则白折腾，重则把数据写到别的设备上去。恢复办法：点【配置…】重新探测到从站即可。
    /// </summary>
    public bool IsSlaveReady => !SlaveProbeFailed && IsValidSlaveId(SlaveIdText);

    /// <summary>开始采集的前提：链路已连上，且从站配置是好的</summary>
    public bool CanAcquire => IsConnected && IsSlaveReady;

    // 下面三个是"开关能不能点"。都带上"已经处于该状态就永远能点"（IsConnected / IsAcquiring）：
    // 否则从站配置在运行中判失败时，会把【断开】【停止采集】一起锁死，连停都停不下来。
    public bool CanToggleConnection => IsConnected || IsSlaveReady;

    public bool CanToggleAcquisition => IsAcquiring || CanAcquire;

    public bool CanTogglePersist => IsAcquiring || IsSlaveReady;

    /// <summary>从站配置 / 连接 / 采集中任一变化，三个开关的可用性都要重算</summary>
    private void NotifyControlAvailability()
    {
        OnPropertyChanged(nameof(IsSlaveReady));
        OnPropertyChanged(nameof(CanAcquire));
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(CanToggleAcquisition));
        OnPropertyChanged(nameof(CanTogglePersist));
    }

    partial void OnSlaveProbeFailedChanged(bool value) => NotifyControlAvailability();

    /// <summary>从站号是否合法（1~247 的整数）：解析规则只此一处，界面提示与能否操作都以它为准</summary>
    private static bool IsValidSlaveId(string? text, out byte unitId) =>
        byte.TryParse(text?.Trim(), out unitId) && unitId is >= 1 and <= 247;

    private static bool IsValidSlaveId(string? text) => IsValidSlaveId(text, out _);

    /// <summary>
    /// 配置弹窗点了【确定】：用户已经确认这组连接参数，先前探测失败的标记清掉。
    /// 首次配置时 PLC 常常还没上电（探测必然失败），不留这个出口的话，
    /// 界面会被卡死在"从站未配置成功"，连试连一下都做不到。
    /// </summary>
    public void ConfirmPlcConfig()
    {
        SlaveProbeFailed = false;
        SlaveProbeStatus = "配置已确认";
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeSlavesCommand))]
    private bool _isProbing;

    private bool CanProbeSlaves() => !IsProbing;

    /// <summary>探测同一 IP:端口 下有哪些从站号在响应，结果填进从站号框（配置弹窗的【探测从站】）</summary>
    [RelayCommand(CanExecute = nameof(CanProbeSlaves))]
    private async Task ProbeSlavesAsync()
    {
        IsProbing = true;
        SlaveProbeFailed = false;
        SlaveProbeStatus = $"探测中：从站 {ProbeStartId}~{ProbeEndId}，每个超时 {ProbeTimeoutMs} ms";

        try
        {
            var progress = new Progress<string>(message => SlaveProbeStatus = message);
            var results = await _connection.ProbeAsync(
                ProbeStartId, ProbeEndId, ProbeTimeoutMs, progress, CancellationToken.None);

            var found = results.Where(r => r.Responded).Select(r => r.UnitId).ToList();

            if (found.Count == 0)
            {
                var reason = results.FirstOrDefault(r => r.UnitId == ProbeStartId)?.Detail ?? "无响应";
                SlaveProbeStatus = $"未发现从站（从站 {ProbeStartId}：{reason}），检查 IP/端口";
                SlaveProbeFailed = true;
                return;
            }

            // 当前选中的从站不在列表里（或还没选），自动切到第一个可用从站
            if (!found.Contains(SlaveId))
            {
                SelectSlave(found[0]);
            }

            SlaveProbeStatus = $"发现 {found.Count} 个从站：{string.Join("、", found)}，当前用 {SlaveId}";
        }
        catch (Exception ex)
        {
            SlaveProbeStatus = $"探测失败：{ex.Message}";
            SlaveProbeFailed = true;
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
        SlaveProbeFailed = false;
    }

    /// <summary>从站号文本变化：能解析成合法从站号才生效，否则只在提示里标红，不改当前从站</summary>
    partial void OnSlaveIdTextChanged(string value)
    {
        NotifyControlAvailability();   // 号码一变，能不能操作就得重新判一次

        var text = value?.Trim() ?? string.Empty;
        if (!IsValidSlaveId(text, out byte unitId))
        {
            if (text.Length > 0)
            {
                SlaveProbeStatus = $"从站号「{text}」不合法：应为 1~247 的整数";
                SlaveProbeFailed = true;
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
        SyncConnectionConfig();

        if (!IsConnected)
        {
            return;
        }

        _ = SwitchSlaveAsync();
    }

    /// <summary>从站号变了：重连并按需恢复采集（ModbusService 的从站号是构造时固定的）</summary>
    private async Task SwitchSlaveAsync()
    {
        // 换从站属于程序内部重连，不是用户按了停止：原来在采就接着采
        bool wasAcquiring = IsAcquiring;

        await StopAcquisitionAsync();
        _connection.Reconnect();

        if (wasAcquiring)
        {
            StartAcquisition();
        }
    }

    // ---------------- 采集控制 ----------------

    /// <summary>开始采集：采集循环 + 触发监听一起起来（没有启用的规则时监听自己会跳过）</summary>
    private void StartAcquisition()
    {
        var modbus = _connection.Modbus;
        if (modbus is null || IsAcquiring || _config.Tags.Count == 0)
        {
            if (_config.Tags.Count == 0)
            {
                Scan.TagStatus = "点位表是空的，先去「采集点位」页扫描或添加点位";
            }

            SyncSwitches();   // 没真正开起来，把开关拨回去
            return;
        }

        _acquisition.Start(modbus);

        IsAcquiring = true;
        StatusText = PersistEnabled
            ? $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {_config.Tags.Count} 个点位 → {_config.Groups.Count} 张表"
            : $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {_config.Tags.Count} 个点位，未落库";
        Log.Information("开始采集：{IntervalMs} ms/次 · 从站 {SlaveId} · {TagCount} 个点位 · 落库 {PersistEnabled}",
            IntervalMs, SlaveId, _config.Tags.Count, PersistEnabled);

        // 采集与触发监听是一套生命周期：开采集就把监听一起起来
        StartTriggerWatch();
    }

    /// <summary>
    /// 停止采集：连触发监听一起停 —— 监听还在跑意味着还在给 PLC 写应答位，
    /// 「已停采集但还在动设备」这种状态没人想要。
    /// </summary>
    private async Task StopAcquisitionAsync()
    {
        await StopTriggerWatchAsync();

        if (!await _acquisition.StopAsync())
        {
            return;
        }

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
                var tag = Dashboard.Tags.FirstOrDefault(t => t.Tag.Address == sample.Tag.Address);
                tag?.Apply(sample);
            }

            foreach (var failure in batch.Failures)
            {
                var tag = Dashboard.Tags.FirstOrDefault(t => t.Tag.Address == failure.Tag.Address);
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
    private void OnAcquisitionFailed(string message) => OnTransportFailed("采集失败（传输层）", message);

    /// <summary>触发监听的传输层故障（采集与监听共用一条链路，通常是一起断的），日志里分开记</summary>
    private void OnTriggerWatchFailed(string message) => OnTransportFailed("触发监听失败（传输层）", message);

    /// <summary>
    /// 传输层故障（断线、PLC 重启）：先落一条 ERROR —— 无人值守时事后只能从 data/logs/ 里查这一段；
    /// 再在界面上提示并标灰。一次故障只记一条，点位级明细不进日志，免得刷屏。
    /// </summary>
    private void OnTransportFailed(string source, string message)
    {
        Log.Error("{Source}：{Message}", source, message);

        _dispatcher.BeginInvoke(() =>
        {
            CommError = $"采集失败：{message}";
            foreach (var tag in Dashboard.Tags)
            {
                tag.MarkOffline();
            }
        });
    }

    /// <summary>落库失败：只提示，不影响实时监控</summary>
    private void OnPersistFailed(string message)
    {
        _dispatcher.BeginInvoke(() => PersistError = $"落库失败：{message}");
        Log.Error("落库失败：{Message}", message);
    }

    partial void OnIntervalMsChanged(int value)
    {
        SyncConnectionConfig();

        if (_acquisition.ApplyIntervalMs(value))
        {
            StatusText = $"采集中：{value} ms/次（采集周期已调整）";
        }
    }

    partial void OnPersistEnabledChanged(bool value) => _acquisition.ApplyPersistEnabled(value);

    private async Task RestartAcquisitionAsync()
    {
        await StopAcquisitionAsync();
        StartAcquisition();
    }

    // ---------------- 触发记录（握手模拟上升沿）：监听生命周期 ----------------

    private void StartTriggerWatch()
    {
        var modbus = _connection.Modbus;
        if (modbus is null || _triggerWatch is not null || !_config.Triggers.Any(t => t.Enabled))
        {
            Trigger.OnWatchSkipped();
            return;
        }

        _triggerWatch = new TriggerWatchService(modbus, [.. _config.Triggers])
        {
            PollIntervalMs = Trigger.TriggerPollIntervalMs
        };
        _triggerWatch.Triggered += Trigger.OnTriggered;
        _triggerWatch.HandshakeCompleted += Trigger.OnHandshakeCompleted;
        _triggerWatch.Alarm += Trigger.OnAlarm;
        _triggerWatch.Failed += OnTriggerWatchFailed;
        _triggerWatch.Start();

        Trigger.OnWatchStarted();
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
        service.Triggered -= Trigger.OnTriggered;
        service.HandshakeCompleted -= Trigger.OnHandshakeCompleted;
        service.Alarm -= Trigger.OnAlarm;
        service.Failed -= OnTriggerWatchFailed;
        await service.StopAsync();
        service.Dispose();

        Trigger.OnWatchStopped();
    }

    /// <summary>规则增删后要重启监听：监听循环用的是启动那一刻的规则快照</summary>
    public async Task RestartTriggerWatchAsync()
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
            await _services.TriggerLogStore.EnsureTableAsync(TriggerLogStore.DefaultTableName);
            await Trigger.RefreshTriggerRecordsAsync();
        }
        catch (Exception ex)
        {
            _ = _dispatcher.BeginInvoke(() => Trigger.TriggerAlarm = $"触发记录表准备失败：{ex.Message}");
            Log.Error(ex, "触发记录表准备失败：{Table}", TriggerLogStore.DefaultTableName);
        }
    }

    // ---------------- 生命周期 ----------------

    public void Dispose()
    {
        Dashboard.Stop();
        Trigger.Stop();
        Scan.CancelIfScanning();
        _acquisition.Dispose();
        _triggerWatch?.Dispose();
        _connection.Dispose();
    }
}
