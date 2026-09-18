using NModbus;
using HmiCore.Models;
using HmiCore.Acquisition;
using HmiCore.Comm;

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
        Assert.Equal(500d, sample.RawValue);
        Assert.Equal(timestamp, sample.Timestamp);
    }

    [Fact]
    public async Task 多寄存器点位_跨分段边界时不被拆开读()
    {
        // 区段长 201，超过 Modbus 单次读上限 125：32 位浮点落在 124~125，
        // 正好压在"0~124 + 125~200"这条天真的切法上 —— 切下去就会两次读各拿一半，高低位配错。
        var modbus = new ScriptedModbusClient();
        for (ushort address = 0; address <= 200; address++)
        {
            modbus.Set(address, address);
        }

        modbus.Set(124, 0x3F80).Set(125, 0x0000);      // 1.0f，高字在前

        var floatTag = new PlcTag(124, "温度", "℃", 1, 0, "温度", PlcDataType.Float32);
        var tags = new[] { NewTag(0), floatTag };

        var batch = await ReadOnce(modbus, tags, new ReadRange(0, 201));

        Assert.Empty(batch.Failures);
        Assert.Equal(1.0, batch.Samples.Single(s => s.Tag == floatTag).RawValue, 6);
        Assert.Equal(2, modbus.BatchReadCalls);        // 0~123 与 124~200：为了不切开点位多让出 1 个寄存器
    }

    [Fact]
    public async Task 多寄存器点位_整段读失败时按点位整段重读()
    {
        var modbus = new ScriptedModbusClient();
        modbus.Set(0, 0x3F80).Set(1, 0x0000);   // 1.0f，高字在前
        modbus.FailBatchReadCovering(3);        // 区段 0~3 里混了个不存在的地址，整段读必然失败

        var floatTag = new PlcTag(0, "温度", "℃", 1, 0, "温度", PlcDataType.Float32);

        var batch = await ReadOnce(modbus, [floatTag], new ReadRange(0, 4));

        Assert.Empty(batch.Failures);
        Assert.Equal(1.0, Assert.Single(batch.Samples).RawValue, 6);
        Assert.Equal(0, modbus.SingleReadCalls);   // 多寄存器点位按整段重读，不是拆成单点拼
        Assert.Equal(2, modbus.LastReadCount);     // 最近一次读的就是它占的那两个寄存器
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
    /// 寄存器值按 ushort 存 —— 32 位浮点要靠两个寄存器的位拼出来，存成 short 就丢了高位。
    /// </summary>
    private sealed class ScriptedModbusClient : IModbusClient
    {
        private readonly Dictionary<ushort, ushort> _values = [];
        private readonly HashSet<ushort> _failingAddresses = [];
        private readonly HashSet<ushort> _badBatchAddresses = [];

        public bool BatchReadFails { get; init; }

        public int BatchReadCalls { get; private set; }

        public int SingleReadCalls { get; private set; }

        /// <summary>最近一次批量读请求了几个寄存器（用来确认多寄存器点位是整段读回来的）</summary>
        public int LastReadCount { get; private set; }

        public ScriptedModbusClient Set(ushort address, ushort value)
        {
            _values[address] = value;
            return this;
        }

        public ScriptedModbusClient FailSingleRead(ushort address)
        {
            _failingAddresses.Add(address);
            return this;
        }

        /// <summary>请求区间覆盖到该地址的批量读整段失败（模拟段内混了不存在的地址），逐点读不受影响</summary>
        public ScriptedModbusClient FailBatchReadCovering(ushort address)
        {
            _badBatchAddresses.Add(address);
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

            return unchecked((short)(_values.TryGetValue(address, out var value) ? value : (ushort)0));
        }

        public ushort[] ReadRegisters(ushort address, ushort count)
        {
            BatchReadCalls++;
            LastReadCount = count;
            if (BatchReadFails)
            {
                throw new SlaveException("模拟：整段读失败（段内有非法地址）");
            }

            var values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                if (_badBatchAddresses.Contains((ushort)(address + i)))
                {
                    throw new SlaveException("模拟：整段读失败（段内有非法地址）");
                }

                values[i] = _values.TryGetValue((ushort)(address + i), out var value) ? value : (ushort)0;
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
