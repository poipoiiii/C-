using PlcCore.Models;

namespace PlcTests;

/// <summary>
/// 机台号 → 机型：机台号前 4 位就是机型代号，这是"该加载哪份配置"的唯一依据，
/// 所以格式与未知代号都得挡住，免得拿错机型的点位去写库。
/// </summary>
public class MachineModelsTests
{
    [Theory]
    [InlineData("AIXP999", "AIXP", "分离封装机")]
    [InlineData("AITN001", "AITN", "辊压机")]
    [InlineData("AIBM123", "AIBM", "成型机")]
    [InlineData("AIIR000", "AIIR", "提纯机")]
    [InlineData("AIPC888", "AIPC", "大片机")]
    [InlineData("aixp999", "AIXP", "分离封装机")]     // 现场偶尔敲小写
    [InlineData("  AIXP999  ", "AIXP", "分离封装机")] // 两端空格
    public void 机台号推出机型(string machineNo, string code, string name)
    {
        Assert.True(MachineModels.TryResolve(machineNo, out var model));
        Assert.Equal(code, model!.Code);
        Assert.Equal(name, model.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AIXP99")]     // 数字不够 3 位
    [InlineData("AIXP9999")]   // 数字多了
    [InlineData("AXP999")]     // 字母不够 4 位
    [InlineData("AIXP99A")]    // 末尾不是数字
    [InlineData("9999999")]
    public void 机台号格式不对_推不出机型(string? machineNo) =>
        Assert.False(MachineModels.TryResolve(machineNo, out _));

    [Fact]
    public void 格式对但代号不在表里_推不出机型()
    {
        // 未知代号要交给用户手工选机型，不能瞎猜成已知机型里的某一个
        Assert.False(MachineModels.TryResolve("AIBX999", out _));
    }

    [Theory]
    [InlineData("AIXP")]
    [InlineData("aixp")]
    [InlineData(" AIXP ")]
    public void 按代号找机型_大小写与空格都不敏感(string code) =>
        Assert.Equal("AIXP", MachineModels.Find(code)!.Code);

    [Theory]
    [InlineData("aixp001", "AIXP001")]
    [InlineData("  aixp001  ", "AIXP001")]
    [InlineData("AIXP999", "AIXP999")]
    [InlineData("aibx999", "AIBX999")]   // 未知代号也照样转大写，认不认得出机型交给 TryResolve 判
    [InlineData("", "")]
    public void 机台号规范成大写(string input, string expected) =>
        Assert.Equal(expected, MachineModels.Normalize(input));

    [Fact]
    public void 自定义机型名_不在已知表里() => Assert.Null(MachineModels.Find("默认"));
}
