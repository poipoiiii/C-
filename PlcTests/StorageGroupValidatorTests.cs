using HmiCore.Models;
using HmiCore.Storage;

namespace PlcTests;

/// <summary>
/// 存储组校验：这些是"拦得住错误配置"的硬约束 ——
/// 漏一条就可能把两个组写到同一张表，或让采集去读界外地址。
/// </summary>
public class StorageGroupValidatorTests
{
    private static PlcTag Tag(ushort address) => new(address, $"点位{address}", string.Empty, 1, 0);

    private static StorageGroup Group(
        string name = "组1",
        string table = "PlcData1",
        StorageMode mode = StorageMode.Long,
        ushort start = 0,
        ushort count = 20) => new(name, table, mode, start, count);

    [Fact]
    public void 正常配置_通过()
    {
        StorageGroupValidator.Validate([Group()], [Tag(0), Tag(1)]);
    }

    [Fact]
    public void 一个组都没有_拦下()
    {
        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate([], []));
    }

    [Fact]
    public void 组名为空_拦下()
    {
        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate([Group(name: "  ")], []));
    }

    [Fact]
    public void 组名重复_只差大小写也算重复()
    {
        var groups = new[] { Group(name: "Line1"), Group(name: "line1", table: "PlcData2", start: 100) };

        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate(groups, []));
    }

    [Fact]
    public void 表名不合法_拦下()
    {
        Assert.Throws<ArgumentException>(() => StorageGroupValidator.Validate([Group(table: "表;DROP")], []));
    }

    [Fact]
    public void 两个组写同一张表_拦下()
    {
        var groups = new[] { Group(name: "组1"), Group(name: "组2", table: "PlcData1", start: 100) };

        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate(groups, []));
    }

    [Fact]
    public void 区段长度为0_拦下()
    {
        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate([Group(count: 0)], []));
    }

    [Fact]
    public void 区段超出地址范围_拦下()
    {
        // 起始 65530 + 20 个 → 结束地址 65549，超过 65535
        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate([Group(start: 65530)], []));
    }

    [Fact]
    public void 两个组区段重叠_拦下()
    {
        var groups = new[] { Group(start: 0, count: 20), Group(name: "组2", table: "PlcData2", start: 19, count: 20) };

        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate(groups, []));
    }

    [Fact]
    public void 区段相邻但不重叠_通过()
    {
        var groups = new[] { Group(start: 0, count: 20), Group(name: "组2", table: "PlcData2", start: 20, count: 20) };

        StorageGroupValidator.Validate(groups, [Tag(0), Tag(20)]);
    }

    [Fact]
    public void 宽表点位数刚好到上限_通过()
    {
        ushort count = DynamicTableStore.MaxPointColumnsInWideTable;
        var tags = Enumerable.Range(0, count).Select(i => Tag((ushort)i)).ToList();

        StorageGroupValidator.Validate([Group(mode: StorageMode.Wide, count: count)], tags);
    }

    [Fact]
    public void 宽表点位数超过上限_拦下()
    {
        ushort count = (ushort)(DynamicTableStore.MaxPointColumnsInWideTable + 1);
        var tags = Enumerable.Range(0, count).Select(i => Tag((ushort)i)).ToList();

        Assert.Throws<InvalidOperationException>(
            () => StorageGroupValidator.Validate([Group(mode: StorageMode.Wide, count: count)], tags));
    }

    [Fact]
    public void 长表点位很多也不管列数上限_通过()
    {
        ushort count = (ushort)(DynamicTableStore.MaxPointColumnsInWideTable + 1);
        var tags = Enumerable.Range(0, count).Select(i => Tag((ushort)i)).ToList();

        StorageGroupValidator.Validate([Group(count: count)], tags);
    }

    [Fact]
    public void 组名与表名的两端空格_校验后去掉()
    {
        var group = Group(name: "  组1  ", table: "  PlcData1  ");

        StorageGroupValidator.Validate([group], []);

        Assert.Equal("组1", group.Name);
        Assert.Equal("PlcData1", group.TableName);
    }

    [Fact]
    public void 三个附加列全关又没有点位_拦下()
    {
        var group = Group(mode: StorageMode.Wide);
        group.UseIdentityColumn = false;
        group.UseTimestampColumn = false;
        group.UseMachineNoColumn = false;

        // 宽表 + 点位列也没有 + 附加列也全关 → 一列都没有
        Assert.Throws<InvalidOperationException>(() => StorageGroupValidator.Validate([group], []));
    }

    [Fact]
    public void 只留机台号列也算有列_通过()
    {
        var group = Group(mode: StorageMode.Wide);
        group.UseIdentityColumn = false;
        group.UseTimestampColumn = false;
        group.UseMachineNoColumn = true;

        StorageGroupValidator.Validate([group], []);
    }
}
