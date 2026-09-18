using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HmiCore;
using HmiCore.Acquisition;
using HmiCore.Comm;
using HmiCore.Config;
using HmiCore.Models;
using HmiCore.Storage;
using PlcHmi.Features.Plc.ViewModels;
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
    private const byte ProbeEndId = 10;

    /// <summary>单个从站的探测超时：短一点，避免探测一轮等太久</summary>
    private const int ProbeTimeoutMs = 400;

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly AppServices _services;

    /// <summary>机型/机台号控制器：机型配置的装载与切换、本机档案（data/machine.json）的读写与一次性迁移</summary>
    private readonly MachineProfileController _profile;

    /// <summary>连接控制器：Modbus 链路的生命周期与连接参数（Ip/Port/SlaveId/IntervalMs）</summary>
    private readonly ConnectionController _connection;

    /// <summary>采集控制器：采集循环的生命周期与落库</summary>
    private readonly AcquisitionController _acquisition;

    /// <summary>外壳状态控制器：会话状态（连接/采集/状态栏/错误/从站探测）与界面可用性规则的唯一来源</summary>
    private readonly ShellStatusController _status;

    private TriggerWatchService? _triggerWatch;

    public MainViewModel() : this(AppServices.Default)
    {
    }

    public MainViewModel(AppServices services)
    {
        _services = services;

        // 机型：命令行 --config / --machine > 本机档案（机台号、上次选的机型）> 已有配置 > 已知机型第一个。
        // 装载、切换与一次性迁移都在控制器里；这里只把它的结论搬到界面上
        _profile = new MachineProfileController(Environment.GetCommandLineArgs());

        // 主题跟着本机档案走，先把配色装好；直接写字段，启动不该触发一次落盘
        _isDarkTheme = string.Equals(_profile.Machine.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        AppTheme.Apply(_isDarkTheme);

        _machineNo = _profile.MachineNo;              // 直接写字段：启动不该触发一次保存

        // 连接与采集各一个控制器：链路生命周期 / 采集循环与落库都收在 HmiCore 里。
        // 控制器不碰界面，状态用事件报回来；面向操作工的文案与日志留在这里
        _connection = new ConnectionController(services, _profile.Machine);
        _acquisition = new AcquisitionController(services, () => Config, () => MachineNo);

        // 状态控制器：会话状态与"哪些开关能点"的规则都在它那里，这里只是转发门面。
        // 从站能否算配置成功要看输入框里的文本，所以把取值方式交给它
        _status = new ShellStatusController(() => SlaveIdText);

        _connection.Connected += OnConnected;
        _connection.ConnectFailed += OnConnectFailed;
        _connection.Disconnected += OnDisconnected;
        _connection.PersistFailed += OnMachinePersistFailed;
        _profile.PersistFailed += OnMachinePersistFailed;
        _acquisition.BatchCompleted += OnBatchCompleted;
        _acquisition.Failed += OnAcquisitionFailed;
        _acquisition.PersistFailed += OnPersistFailed;

        // 状态控制器里的值变了就照原名转出去（界面绑的就是这些名字）；用户拨开关则报事件回来
        _status.PropertyChanged += OnStatusPropertyChanged;
        _status.ConnectionSwitchToggled += OnConnectionSwitchToggled;
        _status.AcquisitionSwitchToggled += OnAcquisitionSwitchToggled;

        // 连接参数唯一一份在连接控制器里：它那边改了值，这里照样发同名通知（界面才刷新）
        _connection.PropertyChanged += OnStatusPropertyChanged;

        // 四个页签各一个视图模型；它们都拿外壳当共享上下文
        Storage = new StorageViewModel(this, services);
        Dashboard = new DashboardViewModel(this, services);
        Scan = new ScanViewModel(this, services);
        Trigger = new TriggerViewModel(this, services);

        Dashboard.Rebuild();
        ApplyConnectionConfig();
        RefreshProfileStatus();

        // 控制器构造期落盘失败时事件还没有订阅者，靠 PersistError 把它带出来
        if (_profile.PersistError is { } persistError)
        {
            StatusText = $"本机档案保存失败：{persistError.Message}";
            Log.Error(persistError, "本机档案保存失败：{File}", AppPaths.MachineFile);
        }

        if (Store.LoadWarning is { } warning)
        {
            StatusText = warning;
            Log.Warning("配置告警：{Warning}", warning);
        }
        else if (_profile.TemplateMissing)
        {
            StatusText = $"没找到机型模板 Templates/{_profile.Profile}.json（安装包可能不完整）：" +
                         $"已按内置默认点位建了 {_profile.Profile}.json，请核对点位表";
            Log.Warning("{Warning}", StatusText);
        }

        if (_profile.MigratedMachineProfile)
        {
            Log.Information("已把机台号/连接参数从机型配置搬到 {File}", AppPaths.MachineFile);
        }

        Log.Information("已启动：机台号 {MachineNo} · 机型 {Profile}（{ConfigFile}）",
            NeedsMachineNo ? "未填" : MachineNo, Store.Profile, Store.FilePath);

        _ = Storage.RefreshTablesAsync();
        _ = Dashboard.RefreshRecordsAsync();
    }

    // ---------------- 共享上下文（给四个页签用） ----------------

    /// <summary>当前机型的配置：点位表、存储组、触发规则都在里面</summary>
    public PlcConfig Config => _profile.Config;

    /// <summary>当前机型的配置读写</summary>
    public ConfigStore Store => _profile.Store;

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

        _profile.Machine.Theme = value ? "Dark" : "Light";
        _profile.SaveMachine();
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
    public bool NeedsMachineNo => _profile.NeedsMachineNo;

    /// <summary>
    /// 机台号还没填对（空着，或不是五个机型之一）：进项目时据此决定要不要弹输入框。
    /// 正常路径下界面是硬校验的，只有手工改过的 machine.json 才可能带着一个认不出的号码进来。
    /// </summary>
    public bool NeedsMachineNoFix => _profile.NeedsMachineNoFix;

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
            return _profile.ResolvedModel is null ? MachineModels.RequirementHint : string.Empty;
        }
    }

    /// <summary>机台号下面那行小字：这个机台号对应的机型</summary>
    public string MachineModelText
    {
        get
        {
            string model = MachineModels.Find(Store.Profile) is { } found
                ? $"{found.Name}（{found.Code}）"
                : $"{Store.Profile}（自定义档案，不在已知机型表里）";

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

        // 存进本机档案并落盘；认不出机型时返回 null，维持当前机型继续跑
        var model = _profile.UpdateMachineNo(machineNo);
        NotifyMachineHeader();

        if (model is null)
        {
            return;
        }

        if (model.Code == Store.Profile)
        {
            StatusText = $"机台号 {machineNo} → 机型 {model.Display}";
            return;
        }

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
    private void RefreshProfileStatus() => ProfileStatus = $"配置文件：{Store.FilePath}";

    /// <summary>本机档案落盘失败（连接参数 / 机台号 / 主题保存）：只在状态栏提示，不打断操作</summary>
    private void OnMachinePersistFailed(Exception ex)
    {
        StatusText = $"本机档案保存失败：{ex.Message}";
        Log.Error(ex, "本机档案保存失败：{File}", AppPaths.MachineFile);
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

            // 先把新机型读出来，读失败就整个放弃、继续用原机型（控制器里的当前机型保持不动）
            _profile.SwitchTo(profile);

            // 四个页签各自按新配置重建（点位表、组表草稿、规则、卡片）
            Scan.OnConfigChanged();
            Trigger.OnConfigChanged();
            Storage.RebuildFromConfig();
            Dashboard.Rebuild();

            RefreshProfileStatus();
            NotifyMachineHeader();

            StatusText = Store.LoadWarning ??
                $"已切换到机型「{Store.Profile}」（{MachineModels.Find(Store.Profile)?.Name ?? "自定义"}）：" +
                $"{Config.Tags.Count} 个点位 · {Config.Groups.Count} 个存储组";
            Log.Information("已切换机型：{Profile}（{ConfigFile}）", Store.Profile, Store.FilePath);
        }
        catch (Exception ex)
        {
            StatusText = $"切换机型失败：{ex.Message}";
            Log.Error(ex, "切换机型失败：{Profile}", profile);
        }
    }

    /// <summary>把当前配置落盘；失败会抛，由调用方决定怎么提示</summary>
    public void SaveConfigNow() => Store.Save(Config);

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
            Log.Error(ex, "配置保存失败：{ConfigFile}", Store.FilePath);
        }

        if (IsAcquiring)
        {
            _ = RestartAcquisitionAsync();
        }
    }

    // ---------------- 连接与采集 ----------------

    /// <summary>
    /// PLC 地址：连接参数的唯一一份在 <see cref="ConnectionController"/> 里，这里只是转发给界面（绑定的仍是这个名字）。
    /// set 写控制器，值真变了才发通知 —— 控制器被外部改也一样刷新。
    /// </summary>
    public string Ip
    {
        get => _connection.Ip;
        set
        {
            if (!_connection.SetIp(value)) return;
            NotifyPlcConfigSummary();
            OnPropertyChanged();
        }
    }

    /// <summary>PLC 端口：转发到连接控制器（语义同 <see cref="Ip"/>）</summary>
    public int Port
    {
        get => _connection.Port;
        set
        {
            if (!_connection.SetPort(value)) return;
            NotifyPlcConfigSummary();
            OnPropertyChanged();
        }
    }

    /// <summary>采集周期：转发到连接控制器，并作用到正在运行的采集（语义同 <see cref="Ip"/>）</summary>
    public int IntervalMs
    {
        get => _connection.IntervalMs;
        set
        {
            if (!_connection.SetIntervalMs(value)) return;
            NotifyPlcConfigSummary();
            OnPropertyChanged();

            // 改采集周期要作用到正在跑的采集循环
            if (_acquisition.ApplyIntervalMs(value))
            {
                StatusText = $"采集中：{value} ms/次（采集周期已调整）";
            }
        }
    }

    /// <summary>
    /// 从站号：转发到连接控制器；已连接时换从站要重连（ModbusService 的从站号是构造时固定的）。
    /// </summary>
    public byte SlaveId
    {
        get => _connection.SlaveId;
        set
        {
            if (!_connection.SetSlaveId(value)) return;
            NotifyPlcConfigSummary();
            OnPropertyChanged();

            if (!IsConnected)
            {
                return;
            }

            _ = SwitchSlaveAsync();
        }
    }

    /// <summary>顶栏「PLC 通信」分区的摘要；参数在配置弹窗里一次填完，这里只展示</summary>
    public string PlcConfigSummary => $"{Ip}:{Port} · 从站 {SlaveId} · {IntervalMs} ms/次";

    private void NotifyPlcConfigSummary() => OnPropertyChanged(nameof(PlcConfigSummary));

    /// <summary>控制器里的值变了就照原名转成外壳自己的通知：界面绑的是外壳，这样控制器侧被外部改也照样刷新</summary>
    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    /// <summary>
    /// 从站号输入框绑的是文本，不是 byte：直接绑 byte 的话边打字边转换，
    /// 中间态（比如只打了半个数字）会弹红色的“未能转换值”校验错误。
    /// </summary>
    [ObservableProperty] private string _slaveIdText = "1";
    [ObservableProperty] private bool _persistEnabled = true;

    /// <summary>链路已连上：状态与可用性规则都在 <see cref="ShellStatusController"/> 里，这里只转发</summary>
    public bool IsConnected { get => _status.IsConnected; set => _status.IsConnected = value; }

    /// <summary>正在采集（转发到状态控制器）</summary>
    public bool IsAcquiring { get => _status.IsAcquiring; set => _status.IsAcquiring = value; }

    /// <summary>状态栏那行（转发；文案在外壳拼好再写进去）</summary>
    public string StatusText { get => _status.StatusText; set => _status.StatusText = value; }

    /// <summary>通信/连接错误（转发）</summary>
    public string CommError { get => _status.CommError; set => _status.CommError = value; }

    /// <summary>落库错误（转发）</summary>
    public string PersistError { get => _status.PersistError; set => _status.PersistError = value; }

    /// <summary>有通信/连接错误：顶栏状态那行据此标红提醒（由状态控制器算出，这里只转发）</summary>
    public bool HasCommError => _status.HasCommError;

    /// <summary>
    /// 工具条上就两个开关，勾选状态即系统状态（勾上=已连接 / 采集中），按钮文字写的是"点下去会发生什么"（连接↔断开、开始采集↔停止采集）。
    /// 用户点击 → 状态控制器报 <see cref="ShellStatusController.ConnectionSwitchToggled"/> 回来走下面的动作；
    /// 状态回写（连接失败、切换机型等）→ 控制器内部只改显示，不再触发动作。
    /// </summary>
    public bool ConnectionSwitch { get => _status.ConnectionSwitch; set => _status.ConnectionSwitch = value; }

    /// <summary>采集开关（语义同 <see cref="ConnectionSwitch"/>）</summary>
    public bool AcquisitionSwitch { get => _status.AcquisitionSwitch; set => _status.AcquisitionSwitch = value; }

    /// <summary>用户拨动连接开关（状态回写不经这里）：勾上就连接，取消就断开</summary>
    private void OnConnectionSwitchToggled(bool value)
    {
        if (value)
        {
            _connection.Connect();
        }
        else
        {
            _ = DisconnectAsync();
        }
    }

    /// <summary>用户拨动采集开关（状态回写不经这里）：勾上就开始采集，取消就停止采集</summary>
    private void OnAcquisitionSwitchToggled(bool value)
    {
        if (value)
        {
            StartAcquisition();
        }
        else
        {
            _ = StopAcquisitionAsync();
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
        _status.RestoreSwitches();
    }

    /// <summary>把本机档案里的连接参数填回界面（启动时用；切机型不动它们）。写进去即经连接控制器，界面由它那边的通知刷新</summary>
    private void ApplyConnectionConfig()
    {
        Ip = _profile.Machine.Ip;
        Port = _profile.Machine.Port;
        IntervalMs = _profile.Machine.IntervalMs;

        // 先赋 SlaveId（未连接时它只记值、不重连），再赋文本：
        // 反过来的话会经由 SelectSlave 弹一句“已切换到从站 N”
        SlaveId = _profile.Machine.SlaveId;
        SlaveIdText = _profile.Machine.SlaveId.ToString();
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

    /// <summary>从站探测提示（转发；文案在外壳拼好再写进去）</summary>
    public string SlaveProbeStatus { get => _status.SlaveProbeStatus; set => _status.SlaveProbeStatus = value; }

    /// <summary>探测/从站号有问题：配置弹窗里那行提示据此标红（转发）</summary>
    public bool SlaveProbeFailed { get => _status.SlaveProbeFailed; set => _status.SlaveProbeFailed = value; }

    /// <summary>
    /// 从站是否配置成功：从站号是 1~247 的整数，且最近一次探测没判失败。
    /// 没配好之前【连接】【开始采集】【写入数据库】一律点不动 —— 拿一个错的从站号去连、去写库，
    /// 轻则白折腾，重则把数据写到别的设备上去。恢复办法：点【配置…】重新探测到从站即可。
    /// （规则在 <see cref="ShellStatusController"/> 里算，这里只转发）
    /// </summary>
    public bool IsSlaveReady => _status.IsSlaveReady;

    /// <summary>开始采集的前提：链路已连上，且从站配置是好的（规则在状态控制器里）</summary>
    public bool CanAcquire => _status.CanAcquire;

    // 下面三个是"开关能不能点"。都带上"已经处于该状态就永远能点"（IsConnected / IsAcquiring）：
    // 否则从站配置在运行中判失败时，会把【断开】【停止采集】一起锁死，连停都停不下来。
    public bool CanToggleConnection => _status.CanToggleConnection;

    public bool CanToggleAcquisition => _status.CanToggleAcquisition;

    public bool CanTogglePersist => _status.CanTogglePersist;

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

    /// <summary>
    /// 探测按钮能否点：配置弹窗的【探测从站】走点击事件（要先取输入框当前值），
    /// 拿不到命令的 CanExecute，所以这里单独给界面一个可绑的开关。
    /// </summary>
    public bool IsProbeIdle => !IsProbing;

    partial void OnIsProbingChanged(bool value) => OnPropertyChanged(nameof(IsProbeIdle));

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
        _status.NotifySlaveIdTextChanged();   // 号码一变，能不能操作就得重新判一次

        var text = value?.Trim() ?? string.Empty;
        if (!ShellStatusController.IsValidSlaveId(text, out byte unitId))
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
        if (modbus is null || IsAcquiring || Config.Tags.Count == 0)
        {
            if (Config.Tags.Count == 0)
            {
                Scan.TagStatus = "点位表是空的，先去「采集点位」页扫描或添加点位";
            }

            _status.RestoreSwitches();   // 没真正开起来，把开关拨回去
            return;
        }

        _acquisition.Start(modbus);

        IsAcquiring = true;
        StatusText = PersistEnabled
            ? $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {Config.Tags.Count} 个点位 → {Config.Groups.Count} 张表"
            : $"采集中：{IntervalMs} ms/次 · 从站 {SlaveId} · {Config.Tags.Count} 个点位，未落库";
        Log.Information("开始采集：{IntervalMs} ms/次 · 从站 {SlaveId} · {TagCount} 个点位 · 落库 {PersistEnabled}",
            IntervalMs, SlaveId, Config.Tags.Count, PersistEnabled);

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
        if (modbus is null || _triggerWatch is not null || !Config.Triggers.Any(t => t.Enabled))
        {
            Trigger.OnWatchSkipped();
            return;
        }

        _triggerWatch = new TriggerWatchService(modbus, [.. Config.Triggers])
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
