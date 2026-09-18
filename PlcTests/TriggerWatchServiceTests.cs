using System.Collections.Concurrent;
using PlcCore.Models;
using PlcCore.Services;

namespace PlcTests;

/// <summary>
/// 握手时序：信号=1 → 记一条 + 应答=1 → PLC 清信号 → 应答=0。
/// 用假客户端驱动，不连真 PLC —— 这段时序错了现场就是漏记或重复记，而且很难事后发现。
/// </summary>
public class TriggerWatchServiceTests
{
    private const ushort SignalAddress = 5;
    private const ushort AckAddress = 6;

    [Fact]
    public async Task 一次完整触发_只记一条_应答先写1再写0()
    {
        var modbus = new FakeModbusClient(SignalAddress);
        var rule = new TriggerRule("测试", SignalAddress, AckAddress);
        using var watch = new TriggerWatchService(modbus, [rule]) { PollIntervalMs = 10 };

        var triggered = new ConcurrentQueue<DateTime>();
        using var completed = new ManualResetEventSlim();
        watch.Triggered += (_, ts) => triggered.Enqueue(ts);
        watch.HandshakeCompleted += _ => completed.Set();

        watch.Start();

        // 等监听真正跑起来再动信号，否则"启动时信号已置位"那条分支会先接管（补完复位、不记录）
        await WaitUntil(() => rule.StatusText == "等待信号", "监听进入等待信号");

        // 1) PLC 置信号：记一条 + 应答写 1
        modbus.Signal = 1;
        await WaitUntil(() => !triggered.IsEmpty, "第一条触发记录");
        Assert.Single(triggered);
        Assert.Single(modbus.Writes);
        Assert.Equal((AckAddress, (ushort)1), modbus.Writes[0]);

        // 2) PLC 清信号：应答写 0，握手完成
        modbus.Signal = 0;
        Assert.True(completed.Wait(TimeSpan.FromSeconds(3)), "信号清零后握手应完成");
        Assert.Equal(2, modbus.Writes.Count);
        Assert.Equal((AckAddress, (ushort)0), modbus.Writes[1]);

        // 3) 第二轮触发不能漏记
        modbus.Signal = 1;
        await WaitUntil(() => triggered.Count >= 2, "第二次触发");

        await watch.StopAsync();
        Assert.Equal(2, triggered.Count);
    }

    [Fact]
    public async Task 启动时信号已置位_补完复位但不补记录()
    {
        var modbus = new FakeModbusClient(SignalAddress) { Signal = 1 };
        var rule = new TriggerRule("测试", SignalAddress, AckAddress);
        using var watch = new TriggerWatchService(modbus, [rule]) { PollIntervalMs = 10 };

        int triggeredCount = 0;
        using var completed = new ManualResetEventSlim();
        watch.Triggered += (_, _) => Interlocked.Increment(ref triggeredCount);
        watch.HandshakeCompleted += _ => completed.Set();

        watch.Start();

        await WaitUntil(() => modbus.Writes.Count >= 1, "补写应答");
        Assert.Equal(0, triggeredCount);
        Assert.Equal((AckAddress, (ushort)1), modbus.Writes[0]);

        modbus.Signal = 0;
        Assert.True(completed.Wait(TimeSpan.FromSeconds(3)), "信号清零后应完成握手复位");

        await watch.StopAsync();
        Assert.Equal(0, triggeredCount);   // 重启程序不该凭空多一条记录
    }

    [Fact]
    public async Task 应答后PLC不清信号_超时报警只报一次()
    {
        var modbus = new FakeModbusClient(SignalAddress);
        var rule = new TriggerRule("测试", SignalAddress, AckAddress) { TimeoutMs = 100 };
        using var watch = new TriggerWatchService(modbus, [rule]) { PollIntervalMs = 10 };

        var alarms = new ConcurrentQueue<string>();
        watch.Alarm += (_, message) => alarms.Enqueue(message);

        watch.Start();
        modbus.Signal = 1;                       // 应答后 PLC 一直不清 0

        await WaitUntil(() => !alarms.IsEmpty, "超时报警");
        await Task.Delay(250);                   // 再跑十几个轮询周期，确认不会重复报

        await watch.StopAsync();
        Assert.Single(alarms);
        Assert.Contains("还没把信号", alarms.First());
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"等待超时：{what}");
    }

    /// <summary>只认信号寄存器，其余读回 0；写操作按顺序记下来给断言用</summary>
    private sealed class FakeModbusClient(ushort signalAddress) : IModbusClient
    {
        private readonly List<(ushort Address, ushort Value)> _writes = [];
        private readonly object _gate = new();

        public short Signal { get; set; }

        public IReadOnlyList<(ushort Address, ushort Value)> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public void Connect(string ip, int port)
        {
        }

        public short ReadRegister(ushort address) => address == signalAddress ? Signal : (short)0;

        public ushort[] ReadRegisters(ushort address, ushort count) => new ushort[count];

        public void WriteSingleRegister(ushort address, ushort value)
        {
            lock (_gate)
            {
                _writes.Add((address, value));
            }
        }

        public void WriteSingleCoil(ushort address, bool value)
        {
        }

        public void Dispose()
        {
        }
    }
}
