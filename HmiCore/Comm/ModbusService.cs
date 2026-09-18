using System.IO;
using System.Net.Sockets;
using NModbus;

namespace HmiCore.Comm;

/// <summary>
/// Modbus TCP 通信封装，后续可整体替换为 HslCommunication。
/// 所有读写都串行化：采集循环、寄存器扫描、触发监听共用同一条 TCP 连接，
/// NModbus 的同步接口不是线程安全的，两个线程同时收发会串包。
///
/// **超时是必须的**：NModbus 默认 ReadTimeout / WriteTimeout = -1（无限等待）、Retries = 3，
/// TcpClient 的收发超时也是 0（无限）。现场设备重启、网线松脱、空闲连接被网关悄悄清掉时，
/// 一次读会永久挂在那里（实测：静默从站下 6 秒仍不返回），而它还握着 <see cref="_gate"/>，
/// 之后界面上的断开、换从站、退出程序都会跟着一起卡死。所以这里统一设上限。
/// </summary>
public class ModbusService : IModbusClient
{
    /// <summary>单次读写的上限：现场 PLC 正常都在几十毫秒内应答，1 秒足够宽松；
    /// 超过就当成"这次通信没成"，宁可快速失败上报，也不要挂着不动</summary>
    private const int IoTimeoutMs = 1000;

    /// <summary>建链的上限：IP 写错或设备没上电时，不设上限的连接要等 SYN 重传（20 秒起步）</summary>
    private const int ConnectTimeoutMs = 3000;

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
        var client = new TcpClient
        {
            // 收发都设上限（socket 层）：NModbus 是同步读写，最终都落到这个 socket 上
            ReceiveTimeout = IoTimeoutMs,
            SendTimeout = IoTimeoutMs
        };

        try
        {
            var connecting = client.ConnectAsync(ip, port);

            // 用 WaitAny 而不是 Wait(超时)：前者对"连接失败"不抛异常，下面才拿得到原始的 SocketException
            if (Task.WaitAny([connecting], ConnectTimeoutMs) != 0)
            {
                throw new TimeoutException(
                    $"连接 {ip}:{port} 超过 {ConnectTimeoutMs} ms 无响应：检查 IP / 端口 / 设备是否上电");
            }

            connecting.GetAwaiter().GetResult();   // 连不上时抛原始异常，界面提示才看得懂
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }

        _tcpClient = client;

        var factory = new ModbusFactory();
        _master = factory.CreateMaster(_tcpClient);

        // NModbus 层的超时（默认 -1 = 无限等）：两层都设上，不依赖某一层的默认值
        _master.Transport.ReadTimeout = IoTimeoutMs;
        _master.Transport.WriteTimeout = IoTimeoutMs;
        _master.Transport.Retries = 0;   // TCP 不重试：同一个动作重复三遍只会把"无响应"拖得更久
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

            try
            {
                return action(_master);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
            {
                // 超时/链路断在界面上原来只是一句英文的 IO 异常，现场看不懂，这里换成能照着做的提示
                throw new IOException(
                    $"与 PLC 通信中断（{IoTimeoutMs} ms 内无响应或链路已断）：检查网线与设备，必要时断开重连。", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // 先关 socket：万一正有一次读卡在网络上，关掉连接会让它立刻以异常退出，而不是继续霸占 _gate
        try
        {
            _tcpClient?.Close();
        }
        catch (Exception)
        {
            // 关连接失败无所谓，下面照样把引用清掉
        }

        // 引用立刻清空：之后进来的读写会拿到"尚未连接"，不再往已关闭的连接上发
        _master = null;
        _tcpClient = null;

        // 有限等待，**绝不无限等** —— 这条路径由界面线程调用（断开 / 换从站 / 退出程序），
        // 拿不到锁也不能把界面卡死。socket 已经关了，持有锁的那次读马上会自己退出。
        if (_gate.Wait(IoTimeoutMs))
        {
            _gate.Release();
        }

        // _gate 不 Dispose：此刻可能还有线程在等它，Dispose 之后那次 Release 会抛 ObjectDisposedException。
        // SemaphoreSlim 只有在用过 AvailableWaitHandle 时才持有非托管资源，这里没用，交给 GC 即可。
    }
}
