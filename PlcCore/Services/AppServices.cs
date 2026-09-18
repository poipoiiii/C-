using PlcCore.Services;

namespace PlcCore.Services;

/// <summary>
/// 组合根：需要 new 具体实现的地方收拢到这一处，视图模型只从这里取依赖。
/// 之所以还要工厂方法而不是直接给实例：<see cref="ModbusService"/> 的从站号是连接时定死的，
/// 每次连接都得新建一个（见 MainViewModel.ConnectInternal）。
/// </summary>
public sealed class AppServices
{
    /// <summary>进程内共用一份</summary>
    public static AppServices Default { get; } = new();

    public ModbusScanService ScanService { get; } = new();

    public SlaveProbeService ProbeService { get; } = new();

    public DynamicTableStore TableStore { get; } = new();

    public TriggerLogStore TriggerLogStore { get; } = new();

    /// <summary>Modbus 客户端工厂；单测可以换成假客户端</summary>
    public Func<byte, IModbusClient> CreateModbusClient { get; init; } = slaveId => new ModbusService(slaveId);
}
