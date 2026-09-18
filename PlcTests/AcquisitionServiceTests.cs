using NModbus;
using PlcCore.Models;
using PlcCore.Services;

namespace PlcTests;

/// <summary>
/// 换算公式（工程值 = 原始值 × Scale + Offset）是整个项目唯一的业务逻辑，
/// 现场点表改错一个 Scale 就是十倍误差，所以单独盯住。
/// 另外盯住"整段批量读失败 → 回退逐点读"：这是采集循环里最容易整段丢数据的回退路径。
/// </summary>
public class AcquisitionServiceTests
{
    [Theory]
    [InlineData((short)500, 0.1, 0, 50.0)]      // 文档里的例子：500 × 0.1 = 50.0
    [InlineData((short)100, 1, -10, 90.0)]      // 带零点偏移
    [InlineData((short)-500, 0.1, 0, -50.0)]    // 有符号：寄存器按 short 解释
    [InlineData((short)0, 0.01, 5, 5.0)]        // 只有偏移
    [InlineData((short)1234, 1, 0, 1234.0)]     // 不换算
    public void 换算公式(short raw, double scale, double offset, double expected)
    {
        var tag = new PlcTag(0, "点位", "", scale, offset);

        Sample sample = PlcAcquisitionService.ToSample(tag, raw, DateTime.UnixEpoch);

        Assert.Equal(expected, sample.EngineeringValue, 6);
    }

    [Fact]
    public void 换算后_原始值与点位身份都保留()
    {
        var tag = new PlcTag(7, "温度", "℃", 0.1, 0);
        var timestamp = new DateTime(2026, 9, 17, 10, 30, 0);

        Sample sample = PlcAcquisitionService.ToSample(tag, 500, timestamp);

        Assert.Same(tag, sample.Tag);
        Assert.Equal((short)500, sample.RawValue);
        Assert.Equal(timestamp, sample.Timestamp);
    }

    [Fact]
    public async Task 整段批量读失败_回退逐点读_段内点位全部产出()
    {
        // 批量读整段抛从站异常（现场常见：段内混了不存在的地址），但逐点读都能读到
        var modbus = new ScriptedModbusClient { BatchReadFails = true }
            .Set(0, 500).Set(1, 100).Set(2, 30);
        var tags = new[] { NewTag(0), NewTag(1), NewTag(2) };

        var batch = await ReadOnce(modbus, tags, new ReadRange(0, 3));

        Assert.Empty(batch.Failures);
        Assert.Equal(3, batch.Samples.Count);
        Assert.Equal(new ushort[] { 0, 1, 2 },
            batch.Samples.Select(s => s.Tag.Address).OrderBy(a => a).ToArray());
        Assert.Equal(3, modbus.SingleReadCalls);   // 确实退化成逐点读了
    }

    [Fact]
    public async Task 逐点读也失败的点位_只进Failures_不拖垮整段()
    {
        var modbus = new ScriptedModbusClient { BatchReadFails = true }
            .Set(0, 500).Set(1, 100).Set(2, 30)
            .FailSingleRead(1);                      // 地址 1 逐点读也读不到
        var tags = new[] { NewTag(0), NewTag(1), NewTag(2) };

        var batch = await ReadOnce(modbus, tags, new ReadRange(0, 3));

        // 好点位照常产出，坏点位只标自己
        Assert.Equal(2, batch.Samples.Count);
        Assert.Equal(new ushort[] { 0, 2 },
            batch.Samples.Select(s => s.Tag.Address).OrderBy(a => a).ToArray());
        Assert.Equal(50.0, batch.Samples.Single(s => s.Tag.Address == 0).EngineeringValue, 6);

        var failure = Assert.Single(batch.Failures);
        Assert.Equal((ushort)1, failure.Tag.Address);
        Assert.False(string.IsNullOrEmpty(failure.Reason));
    }

    [Fact]
    public async Task 批量读成功时_不退化成逐点读()
    {
        var modbus = new ScriptedModbusClient().Set(0, 500).Set(1, 100).Set(2, 30);
        var tags = new[] { NewTag(0), NewTag(1), NewTag(2) };

        var batch = await ReadOnce(modbus, tags, new ReadRange(0, 3));

        Assert.Empty(batch.Failures);
        Assert.Equal(3, batch.Samples.Count);
        Assert.Equal(1, modbus.BatchReadCalls);    // 整段一次读完
        Assert.Equal(0, modbus.SingleReadCalls);   // 不走逐点回退
    }

    private static PlcTag NewTag(ushort address) => new(address, $"点位{address}", "", 0.1, 0);

    /// <summary>启动采集循环跑一轮、抓回第一个 BatchCompleted，然后停掉（周期调大保证只读一轮）</summary>
    private static async Task<AcquisitionBatch> ReadOnce(
        IModbusClient modbus, IReadOnlyList<PlcTag> tags, ReadRange range)
    {
        using var service = new PlcAcquisitionService(modbus, tags, [range])
        {
            IntervalMs = 60_000,
            PersistEnabled = false
        };

        var done = new TaskCompletionSource<AcquisitionBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BatchCompleted += batch => done.TrySetResult(batch);

        service.Start();
        try
        {
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    /// <summary>
    /// 可脚本化的假客户端：批量读可整体失败（模拟段内地址非法），单点读可按地址单独失败；
    /// 记下两类调用的次数，用来断言"批量读成功时不会退化成逐点读"。
    /// </summary>
    private sealed class ScriptedModbusClient : IModbusClient
    {
        private readonly Dictionary<ushort, short> _values = [];
        private readonly HashSet<ushort> _failingAddresses = [];

        public bool BatchReadFails { get; init; }

        public int BatchReadCalls { get; private set; }

        public int SingleReadCalls { get; private set; }

        public ScriptedModbusClient Set(ushort address, short value)
        {
            _values[address] = value;
            return this;
        }

        public ScriptedModbusClient FailSingleRead(ushort address)
        {
            _failingAddresses.Add(address);
            return this;
        }

        public void Connect(string ip, int port)
        {
        }

        public short ReadRegister(ushort address)
        {
            SingleReadCalls++;
            if (_failingAddresses.Contains(address))
            {
                throw new SlaveException("模拟：该地址逐点读也失败");
            }

            return _values.TryGetValue(address, out var value) ? value : (short)0;
        }

        public ushort[] ReadRegisters(ushort address, ushort count)
        {
            BatchReadCalls++;
            if (BatchReadFails)
            {
                throw new SlaveException("模拟：整段读失败（段内有非法地址）");
            }

            var values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = _values.TryGetValue((ushort)(address + i), out var value) ? (ushort)value : (ushort)0;
            }

            return values;
        }

        public void WriteSingleRegister(ushort address, ushort value)
        {
        }

        public void WriteSingleCoil(ushort address, bool value)
        {
        }

        public void Dispose()
        {
        }
    }
}
