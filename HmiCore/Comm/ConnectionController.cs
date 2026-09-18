using CommunityToolkit.Mvvm.ComponentModel;
using HmiCore.Config;
using HmiCore.Models;

namespace HmiCore.Comm;

/// <summary>
/// 连接控制器：Modbus 链路的生命周期与连接参数 —— 连接 / 断开 / 换从站重连 / 从站探测。
/// 只管链路，不碰界面：状态用事件报出去，中文文案与日志留在视图模型
/// （控制器里不允许出现任何 WPF 类型，线程切换也由视图模型自己处理）。
///
/// 连接参数（<see cref="Ip"/> / <see cref="Port"/> / <see cref="SlaveId"/> / <see cref="IntervalMs"/>）
/// 在这里是**唯一一份**：这样「连上就把参数记进本机档案」不依赖界面。
/// 视图模型上的同名属性只是转发（get 读这里、set 写这里），没有第二份存储；
/// 这里改了值也会发 <see cref="ObservableObject.PropertyChanged"/>，界面跟着刷新。
///
/// <see cref="ModbusService"/> 的从站号是构造时固定的，所以换从站只能重建客户端重连。
/// </summary>
public sealed class ConnectionController : ObservableObject, IDisposable
{
    private readonly AppServices _services;

    /// <summary>本机档案（data/machine.json）：连接参数连上一次就记进这里，下次启动不用重填</summary>
    private readonly MachineProfile _machine;

    private IModbusClient? _modbus;

    public ConnectionController(AppServices services, MachineProfile machine)
    {
        _services = services;
        _machine = machine;
    }

    private string _ip = "192.168.1.88";
    private int _port = 502;
    private byte _slaveId = 1;
    private int _intervalMs = 1000;

    /// <summary>PLC 地址（连接参数唯一一份，视图模型只是转发）</summary>
    public string Ip => _ip;

    /// <summary>PLC 端口（连接参数唯一一份，视图模型只是转发）</summary>
    public int Port => _port;

    /// <summary>从站号（连接参数唯一一份，视图模型只是转发）</summary>
    public byte SlaveId => _slaveId;

    /// <summary>采集周期（毫秒）：跟着设备存进本机档案，不跟着机型走（连接参数唯一一份，视图模型只是转发）</summary>
    public int IntervalMs => _intervalMs;

    /// <summary>改 PLC 地址：唯一写入入口，值真变了才发变更通知并返回 true（视图模型转发写这里，没有第二份存储）</summary>
    public bool SetIp(string value) => SetProperty(ref _ip, value, nameof(Ip));

    /// <summary>改 PLC 端口（语义同 <see cref="SetIp"/>）</summary>
    public bool SetPort(int value) => SetProperty(ref _port, value, nameof(Port));

    /// <summary>改从站号（语义同 <see cref="SetIp"/>）</summary>
    public bool SetSlaveId(byte value) => SetProperty(ref _slaveId, value, nameof(SlaveId));

    /// <summary>改采集周期（语义同 <see cref="SetIp"/>）</summary>
    public bool SetIntervalMs(int value) => SetProperty(ref _intervalMs, value, nameof(IntervalMs));

    /// <summary>已连接的 Modbus 客户端；没连接时为 null</summary>
    public IModbusClient? Modbus => _modbus;

    public bool IsConnected { get; private set; }

    /// <summary>连上了（含换从站后的重连），参数已写进本机档案</summary>
    public event Action? Connected;

    /// <summary>连接失败：链路已清掉，界面自己决定怎么提示</summary>
    public event Action<Exception>? ConnectFailed;

    /// <summary>链路已断开</summary>
    public event Action? Disconnected;

    /// <summary>连接参数落盘失败（连接本身是好的，不打断操作）</summary>
    public event Action<Exception>? PersistFailed;

    /// <summary>
    /// 连接 PLC。只建链路，不自动开采集 —— 采集由采集开关单独控制，免得连上就往库里写数据。
    /// 失败时链路清空并报 <see cref="ConnectFailed"/>，不往外抛。
    /// </summary>
    public void Connect()
    {
        try
        {
            _modbus = _services.CreateModbusClient(SlaveId);
            _modbus.Connect(Ip, Port);

            IsConnected = true;
            Connected?.Invoke();
            PersistConnectionConfig();
        }
        catch (Exception ex)
        {
            _modbus?.Dispose();
            _modbus = null;
            IsConnected = false;
            ConnectFailed?.Invoke(ex);
        }
    }

    /// <summary>断开链路（采集与触发监听的停止由调用方负责，它们有自己的生命周期）</summary>
    public void Disconnect()
    {
        _modbus?.Dispose();
        _modbus = null;
        IsConnected = false;
        Disconnected?.Invoke();
    }

    /// <summary>
    /// 换从站后的重连：先扔掉旧客户端再按新从站号连。
    /// 这是程序内部重连、不是用户按了断开，所以不报 <see cref="Disconnected"/>（界面状态不会闪一下“已断开”）。
    /// </summary>
    public void Reconnect()
    {
        _modbus?.Dispose();
        _modbus = null;
        IsConnected = false;
        Connect();
    }

    /// <summary>探测同一 IP:端口 下有哪些从站号在响应（配置弹窗的【探测从站】），结果的解读交给视图模型</summary>
    public Task<IReadOnlyList<SlaveProbeResult>> ProbeAsync(
        byte startId, byte endId, int timeoutMs, IProgress<string>? progress, CancellationToken token) =>
        _services.ProbeService.ProbeAsync(Ip, Port, startId, endId, timeoutMs, progress, token);

    /// <summary>连接参数写进本机档案：连上一次就记住，下次启动不用重填（跟着设备走，不跟着机型走）</summary>
    private void PersistConnectionConfig()
    {
        _machine.Ip = Ip;
        _machine.Port = Port;
        _machine.SlaveId = SlaveId;
        _machine.IntervalMs = IntervalMs;

        try
        {
            MachineStore.Save(_machine);
        }
        catch (Exception ex)
        {
            PersistFailed?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        _modbus?.Dispose();
        _modbus = null;
    }
}
