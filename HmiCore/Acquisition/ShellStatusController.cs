using CommunityToolkit.Mvvm.ComponentModel;

namespace HmiCore.Acquisition;

/// <summary>
/// 外壳状态控制器：会话状态（连接 / 采集 / 状态栏文案 / 通信与落库错误 / 从站探测）与
/// 「界面哪些按钮能点」的派生规则都收在这里，是这些状态的**唯一来源**。
///
/// 视图模型上的同名成员只是转发门面（get/set 都走这里），把 <see cref="ObservableObject.PropertyChanged"/>
/// 原样转发给自己的订阅者即可，规则本身不重算 —— 这样规则能脱离 WPF 单测（见 PlcTests）。
///
/// 只管状态与可用性，不碰界面：开关被用户拨动时用 <see cref="ConnectionSwitchToggled"/> /
/// <see cref="AcquisitionSwitchToggled"/> 报出去，真正连/断、开/停由视图模型编排
/// （控制器里不允许出现任何 WPF 类型）。中文文案由视图模型拼好写进 <see cref="StatusText"/>。
/// </summary>
public sealed class ShellStatusController : ObservableObject
{
    /// <summary>从站号输入框里的文本：从站算不算配置成功，靠这段文本 + 最近一次探测结果判定</summary>
    private readonly Func<string?> _slaveIdText;

    private bool _isConnected;
    private bool _isAcquiring;
    private string _statusText = "未连接";
    private string _commError = string.Empty;
    private string _persistError = string.Empty;
    private string _slaveProbeStatus = "未探测";
    private bool _slaveProbeFailed;
    private bool _connectionSwitch;
    private bool _acquisitionSwitch;

    /// <summary>正在由状态回写开关：挡住开关的变更通知，免得连接/断开动作被反过来又触发一遍</summary>
    private bool _syncingSwitches;

    public ShellStatusController(Func<string?> slaveIdText) => _slaveIdText = slaveIdText;

    // ---------------- 会话状态 ----------------

    /// <summary>链路已连上</summary>
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (!SetProperty(ref _isConnected, value))
            {
                return;
            }

            RestoreSwitches();
            NotifyControlAvailability();
        }
    }

    /// <summary>正在采集</summary>
    public bool IsAcquiring
    {
        get => _isAcquiring;
        set
        {
            if (!SetProperty(ref _isAcquiring, value))
            {
                return;
            }

            RestoreSwitches();
            NotifyControlAvailability();
        }
    }

    /// <summary>状态栏那行</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>通信/连接错误</summary>
    public string CommError
    {
        get => _commError;
        set
        {
            if (SetProperty(ref _commError, value))
            {
                OnPropertyChanged(nameof(HasCommError));
            }
        }
    }

    /// <summary>落库错误</summary>
    public string PersistError
    {
        get => _persistError;
        set => SetProperty(ref _persistError, value);
    }

    /// <summary>从站探测/切换提示</summary>
    public string SlaveProbeStatus
    {
        get => _slaveProbeStatus;
        set => SetProperty(ref _slaveProbeStatus, value);
    }

    /// <summary>探测/从站号有问题：配置弹窗里那行提示据此标红</summary>
    public bool SlaveProbeFailed
    {
        get => _slaveProbeFailed;
        set
        {
            if (SetProperty(ref _slaveProbeFailed, value))
            {
                NotifyControlAvailability();
            }
        }
    }

    // ---------------- 工具条上那两个开关 ----------------

    /// <summary>
    /// 连接开关（勾选状态即系统状态）：用户拨动时经 <see cref="ConnectionSwitchToggled"/> 报出去，
    /// 状态回写（<see cref="RestoreSwitches"/>）时只改显示、不报事件。
    /// </summary>
    public bool ConnectionSwitch
    {
        get => _connectionSwitch;
        set
        {
            if (!SetProperty(ref _connectionSwitch, value) || _syncingSwitches)
            {
                return;
            }

            ConnectionSwitchToggled?.Invoke(value);
        }
    }

    /// <summary>采集开关（语义同 <see cref="ConnectionSwitch"/>）</summary>
    public bool AcquisitionSwitch
    {
        get => _acquisitionSwitch;
        set
        {
            if (!SetProperty(ref _acquisitionSwitch, value) || _syncingSwitches)
            {
                return;
            }

            AcquisitionSwitchToggled?.Invoke(value);
        }
    }

    /// <summary>用户拨动了连接开关（不是状态回写）：视图模型据此发起连接 / 断开</summary>
    public event Action<bool>? ConnectionSwitchToggled;

    /// <summary>用户拨动了采集开关（不是状态回写）：视图模型据此开始 / 停止采集</summary>
    public event Action<bool>? AcquisitionSwitchToggled;

    // ---------------- 派生规则（只在这里算一遍） ----------------

    /// <summary>有通信/连接错误：顶栏状态那行据此标红提醒</summary>
    public bool HasCommError => _commError.Length > 0;

    /// <summary>
    /// 从站是否配置成功：从站号是 1~247 的整数，且最近一次探测没判失败。
    /// 没配好之前【连接】【开始采集】【写入数据库】一律点不动 —— 拿一个错的从站号去连、去写库，
    /// 轻则白折腾，重则把数据写到别的设备上去。恢复办法：点【配置…】重新探测到从站即可。
    /// </summary>
    public bool IsSlaveReady => !_slaveProbeFailed && IsValidSlaveId(_slaveIdText());

    /// <summary>开始采集的前提：链路已连上，且从站配置是好的</summary>
    public bool CanAcquire => IsConnected && IsSlaveReady;

    // 下面三个是"开关能不能点"。都带上"已经处于该状态就永远能点"（IsConnected / IsAcquiring）：
    // 否则从站配置在运行中判失败时，会把【断开】【停止采集】一起锁死，连停都停不下来。
    public bool CanToggleConnection => IsConnected || IsSlaveReady;

    public bool CanToggleAcquisition => IsAcquiring || CanAcquire;

    public bool CanTogglePersist => IsAcquiring || IsSlaveReady;

    /// <summary>从站号是否合法（1~247 的整数）：解析规则只此一处，界面提示与能否操作都以它为准</summary>
    public static bool IsValidSlaveId(string? text, out byte unitId) =>
        byte.TryParse(text?.Trim(), out unitId) && unitId is >= 1 and <= 247;

    public static bool IsValidSlaveId(string? text) => IsValidSlaveId(text, out _);

    /// <summary>从站号文本变了（输入框或内部切换）：从站是否算配置成功、开关能不能点，都要重判一次</summary>
    public void NotifySlaveIdTextChanged() => NotifyControlAvailability();

    /// <summary>把两个开关拨到与当前状态一致（动作没生效时靠它把开关还原）</summary>
    public void RestoreSwitches()
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

    /// <summary>从站配置 / 连接 / 采集中任一变化，三个开关的可用性都要重算</summary>
    private void NotifyControlAvailability()
    {
        OnPropertyChanged(nameof(IsSlaveReady));
        OnPropertyChanged(nameof(CanAcquire));
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(CanToggleAcquisition));
        OnPropertyChanged(nameof(CanTogglePersist));
    }
}
