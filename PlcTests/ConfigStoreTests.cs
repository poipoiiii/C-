using PlcHmi.Services;

namespace PlcTests;

/// <summary>机型名会被拼进配置文件名，白名单既是命名规范也是路径穿越的防线。</summary>
public class ConfigStoreTests
{
    [Theory]
    [InlineData("默认")]
    [InlineData("挤出机A")]
    [InlineData("line_1")]
    [InlineData("1号机")]
    [InlineData("A-1")]
    public void 合法机型名_通过(string name)
    {
        Assert.True(ConfigStore.IsValidProfileName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil")]      // 路径穿越
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a.json")]       // 点号不在白名单，避免出现 机型.json.json
    [InlineData("a b")]
    [InlineData("机型?")]
    public void 非法机型名_拦下(string? name)
    {
        Assert.False(ConfigStore.IsValidProfileName(name));
    }

    [Fact]
    public void 机型名超过32字符_拦下()
    {
        Assert.False(ConfigStore.IsValidProfileName(new string('机', 33)));
    }
}
