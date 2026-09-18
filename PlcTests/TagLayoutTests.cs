using HmiCore.Models;

namespace PlcTests;

/// <summary>
/// 点位在寄存器上的落位：32/64 位量横跨几个寄存器，它盖住的那几个单寄存器点位不算数据 ——
/// 表里一个数据一列，不该把大数的半截字节也列一列。
/// </summary>
public class TagLayoutTests
{
    private static PlcTag Tag(ushort address, string name, PlcDataType type = PlcDataType.Int16) =>
        new(address, name, string.Empty, 1, 0, name, type);

    [Fact]
    public void 不重叠时_全都算数()
    {
        var tags = new[] { Tag(0, "温度"), Tag(1, "压力"), Tag(2, "产量") };

        Assert.Equal(new[] { "温度", "压力", "产量" }, TagLayout.EffectiveTags(tags).Select(t => t.Name));
    }

    [Fact]
    public void 三十二位点位盖住后面的单寄存器点位_被盖住的不算数()
    {
        // 温度占 0~1（32 位浮点），压力挂在 1 上 —— 1 本来就是温度的一部分
        var 温度 = Tag(0, "温度", PlcDataType.Float32);
        var 压力 = Tag(1, "压力");
        var 产量 = Tag(2, "产量");

        Assert.Equal(new[] { "温度", "产量" },
            TagLayout.EffectiveTags([温度, 压力, 产量]).Select(t => t.Name));

        var placement = TagLayout.Resolve([温度, 压力, 产量]).Single(p => p.Tag == 压力);
        Assert.Same(温度, placement.CoveredBy);
    }

    [Fact]
    public void 六十四位点位盖住三个寄存器()
    {
        var 累计 = Tag(0, "累计", PlcDataType.Float64);
        var a = Tag(1, "a");
        var b = Tag(2, "b");
        var c = Tag(3, "c");
        var d = Tag(4, "d");

        Assert.Equal(new[] { "累计", "d" },
            TagLayout.EffectiveTags([累计, a, b, c, d]).Select(t => t.Name));
    }

    [Fact]
    public void 起始地址相同时_占得宽的那个算()
    {
        var 半截 = Tag(0, "半截");
        var 温度 = Tag(0, "温度", PlcDataType.Float32);

        Assert.Equal(new[] { "温度" }, TagLayout.EffectiveTags([半截, 温度]).Select(t => t.Name));
    }

    [Fact]
    public void 接在后面的多寄存器点位_不受前面的影响()
    {
        var a = Tag(0, "a");
        var b = Tag(1, "b", PlcDataType.Float32);   // 占 1~2

        Assert.Equal(new[] { "a", "b" }, TagLayout.EffectiveTags([a, b]).Select(t => t.Name));
    }

    [Fact]
    public void 传入顺序乱_结果一样()
    {
        var 温度 = Tag(0, "温度", PlcDataType.Float32);
        var 压力 = Tag(1, "压力");

        Assert.Equal(new[] { "温度" }, TagLayout.EffectiveTags([压力, 温度]).Select(t => t.Name));
    }
}
