using PlcHmi.Models;
using PlcHmi.Services;

namespace PlcTests;

/// <summary>
/// 换算公式（工程值 = 原始值 × Scale + Offset）是整个项目唯一的业务逻辑，
/// 现场点表改错一个 Scale 就是十倍误差，所以单独盯住。
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
}
