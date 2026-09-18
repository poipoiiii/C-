using System.Net.Sockets;
using NModbus;

namespace HmiCore.Comm;

/// <summary>
/// Modbus TCP 通信封装，后续可整体替换为 HslCommunication。
/// 所有读写都串行化：采集循环、寄存器扫描、触发监听共用同一条 TCP 连接，
/// NModbus 的同步接口不是线程安全的，两个线程同时收发会串包。
/// </summary>
public class ModbusService : IModbusClient
{
    private readonly byte _slaveId; /// 从站 ID

    /// <summary>连接级互斥：把同一时刻的 Modbus 事务限制成一个</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);   /// 信号量，一个信号量一个线程

    private TcpClient? _tcpClient;  /// TCP 连接
    private IModbusMaster? _master; /// Modbus 主站

    public ModbusService(byte slaveId = 1)  
    {
        _slaveId = slaveId; 
    }

    public void Connect(string ip, int port)
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(ip, port);

        var factory = new ModbusFactory();
        _master = factory.CreateMaster(_tcpClient);
    }

    /// <summary>
    /// 读取 1 个保持寄存器，地址为 0-based 协议地址
    /// </summary>
    public short ReadRegister(ushort address)
    {
        return Run(master =>
        {
            ushort[] result = master.ReadHoldingRegisters(_slaveId, address, 1);
            return (short)result[0];
        });
    }

    /// <summary>
    /// 批量读取连续保持寄存器，地址为 0-based 协议地址。
    /// 地址段非法时从站会回异常码（NModbus 抛 SlaveException），调用方自行捕获。
    /// </summary>
    public ushort[] ReadRegisters(ushort address, ushort count)
    {
        return Run(master => master.ReadHoldingRegisters(_slaveId, address, count));
    }

    /// <summary>写 1 个保持寄存器（FC06）</summary>
    public void WriteSingleRegister(ushort address, ushort value)
    {
        Run(master =>
        {
            master.WriteSingleRegister(_slaveId, address, value);
            return true;
        });
    }

    /// <summary>写 1 个线圈（FC05）</summary>
    public void WriteSingleCoil(ushort address, bool value)
    {
        Run(master =>
        {
            master.WriteSingleCoil(_slaveId, address, value);
            return true;
        });
    }

    /// <summary>取到已连接的 master 并独占执行一次收发</summary>
    private T Run<T>(Func<IModbusMaster, T> action)
    {
        _gate.Wait();
        try
        {
            if (_master is null)
            {
                throw new InvalidOperationException("Modbus 尚未连接，请先调用 Connect。");
            }

            return action(_master);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _master?.Dispose();
            _tcpClient?.Dispose();
            _master = null;
            _tcpClient = null;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }
}
