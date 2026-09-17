using PlcHmi.Models;
using PlcHmi.Services;

namespace PlcTests;

/// <summary>
/// 动态目标表的拼 SQL 逻辑。表名/列名不能参数化，白名单校验是防注入的唯一防线，
/// 这些行为一旦回归会直接变成注入漏洞或建表失败，必须有测试盯着。
/// </summary>
public class DynamicTableStoreTests
{
    [Theory]
    [InlineData("Tag_1")]
    [InlineData("_tmp2024")]
    [InlineData("点位表")]
    [InlineData("温度_1")]
    public void 合法标识符_通过(string name)
    {
        DynamicTableStore.EnsureValidIdentifier(name, "表名");
    }

    [Theory]
    [InlineData("")]            // 空
    [InlineData("   ")]         // 纯空白
    [InlineData("1号表")]        // 数字开头
    [InlineData("tag-1")]       // 短横线不在白名单里
    [InlineData("a b")]         // 空格
    [InlineData("tag;DROP")]    // 分号
    [InlineData("tag]")]        // 右中括号（会被转义，索性不收）
    [InlineData("表名';--")]     // 引号 + 注释
    public void 非法标识符_拦下(string name)
    {
        Assert.Throws<ArgumentException>(() => DynamicTableStore.EnsureValidIdentifier(name, "表名"));
    }

    [Fact]
    public void 标识符超过64字符_拦下()
    {
        string name = new('a', 65);
        Assert.Throws<ArgumentException>(() => DynamicTableStore.EnsureValidIdentifier(name, "表名"));
    }

    [Fact]
    public void 宽表列名重复_拦下()
    {
        List<PlcTag> tags =
        [
            new PlcTag(0, "温度", "℃", 1, 0, "Temp"),
            new PlcTag(1, "压力", "kPa", 1, 0, "temp")   // 只差大小写，SQL Server 默认不区分
        ];

        Assert.Throws<ArgumentException>(() => DynamicTableStore.EnsureColumnsUsable(tags));
    }

    [Fact]
    public void 宽表列名非法_拦下()
    {
        List<PlcTag> tags = [new PlcTag(0, "温度", "℃", 1, 0, "温 度")];

        Assert.Throws<ArgumentException>(() => DynamicTableStore.EnsureColumnsUsable(tags));
    }

    [Fact]
    public void 长表建表语句_只有固定列()
    {
        string script = DynamicTableStore.BuildCreateScript("表A", StorageMode.Long, []);

        Assert.Contains("CREATE TABLE [表A]", script);
        Assert.Contains("[Timestamp] DATETIME2 NOT NULL", script);
        Assert.Contains("[RegisterAddress] INT NOT NULL", script);
        Assert.Contains("[RawValue] SMALLINT NOT NULL", script);
        Assert.Contains("[EngineeringValue] FLOAT NOT NULL", script);
        Assert.Contains("[Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY", script);
    }

    [Fact]
    public void 宽表建表语句_每个点位一列且可空()
    {
        List<PlcTag> tags =
        [
            new PlcTag(0, "温度", "℃", 0.1, 0),
            new PlcTag(1, "压力", "kPa", 0.01, 0)
        ];

        string script = DynamicTableStore.BuildCreateScript("表B", StorageMode.Wide, tags);

        Assert.Contains("[温度] FLOAT NULL", script);
        Assert.Contains("[压力] FLOAT NULL", script);
        // 宽表不写长表那几列，否则一行一个时刻的语义就乱了
        Assert.DoesNotContain("[RegisterAddress]", script);
        Assert.DoesNotContain("[EngineeringValue]", script);
    }

    [Fact]
    public void 表名非法_建表语句直接抛()
    {
        Assert.Throws<ArgumentException>(() => DynamicTableStore.BuildCreateScript("表;DROP", StorageMode.Long, []));
    }

    [Fact]
    public void 关掉附加列_建表语句里就没有Id和时间戳()
    {
        var shape = new TableShape(UseIdentityColumn: false, UseTimestampColumn: false);
        List<PlcTag> tags = [new PlcTag(0, "温度", "℃", 0.1, 0)];

        string script = DynamicTableStore.BuildCreateScript("表C", StorageMode.Wide, tags, shape);

        Assert.Contains("[温度] FLOAT NULL", script);
        Assert.DoesNotContain("[Id]", script);
        Assert.DoesNotContain("[Timestamp]", script);
    }

    [Fact]
    public void 只留Id_长表建表语句里没有时间戳()
    {
        var shape = new TableShape(UseIdentityColumn: true, UseTimestampColumn: false);

        string script = DynamicTableStore.BuildCreateScript("表D", StorageMode.Long, [], shape);

        Assert.Contains("[Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY", script);
        Assert.DoesNotContain("[Timestamp] DATETIME2", script);
        Assert.Contains("[RegisterAddress] INT NOT NULL", script);
    }

    [Fact]
    public void 附加列全关又没有点位_建表语句直接抛()
    {
        var shape = new TableShape(UseIdentityColumn: false, UseTimestampColumn: false);

        // 宽表 + 点位列也没有 → 一列都没有，不能吐一句无效的 CREATE TABLE
        Assert.Throws<ArgumentException>(
            () => DynamicTableStore.BuildCreateScript("表E", StorageMode.Wide, [], shape));
    }

    [Fact]
    public void 宽表点位数上限_跟着附加列多少走()
    {
        Assert.Equal(DynamicTableStore.MaxColumnsPerTable,
            DynamicTableStore.MaxPointColumns(new TableShape(false, false)));
        Assert.Equal(DynamicTableStore.MaxColumnsPerTable - 1,
            DynamicTableStore.MaxPointColumns(new TableShape(true, false)));
        Assert.Equal(DynamicTableStore.MaxPointColumnsInWideTable,
            DynamicTableStore.MaxPointColumns(TableShape.Default));
    }
}
