using PlcCore.Models;

namespace PlcTests;

/// <summary>点位的默认值与联动：名称 ↔ 宽表列名、人读地址、归组展示。</summary>
public class PlcTagTests
{
    [Fact]
    public void 构造时_列名默认跟名称一致()
    {
        var tag = new PlcTag(0, "温度", "℃", 0.1, 0);

        Assert.Equal("温度", tag.ColumnName);
    }

    [Fact]
    public void 改名称且列名没被单独改过_列名跟着变()
    {
        var tag = new PlcTag(0, "温度", "℃", 0.1, 0);

        tag.Name = "热封温度";

        Assert.Equal("热封温度", tag.ColumnName);
    }

    [Fact]
    public void 列名被单独改过_再改名称不动列名()
    {
        var tag = new PlcTag(0, "温度", "℃", 0.1, 0);
        tag.ColumnName = "Temp1";     // 手改过：可能与既有表结构对齐，不能被名称覆盖

        tag.Name = "热封温度";

        Assert.Equal("Temp1", tag.ColumnName);
    }

    [Theory]
    [InlineData((ushort)0, "40001")]
    [InlineData((ushort)99, "40100")]
    [InlineData((ushort)65534, "465535")]
    public void 人读地址_协议地址加40001(ushort address, string expected)
    {
        var tag = new PlcTag(address, "点位", "", 1, 0);

        Assert.Equal(expected, tag.DisplayAddress);
    }

    [Fact]
    public void 未归组_显示为未归组()
    {
        var tag = new PlcTag(0, "点位", "", 1, 0);

        Assert.Equal("未归组", tag.GroupText);
    }
}
