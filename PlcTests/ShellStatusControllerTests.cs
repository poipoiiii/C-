using HmiCore.Acquisition;

namespace PlcTests;

/// <summary>
/// 外壳状态控制器：把"哪些按钮能点"的规则从视图模型搬到 HmiCore 后单测。
/// 这些规则是硬约束 —— 判松了会拿一个错的从站号去连、去写库，判紧了会把【断开】【停止采集】一起锁死。
/// </summary>
public class ShellStatusControllerTests
{
    /// <summary>造一个"从站号文本"可控的状态控制器：这段文本就是配置弹窗里那个输入框的值</summary>
    private static ShellStatusController Status(string? slaveIdText = "1")
    {
        string? text = slaveIdText;
        return new ShellStatusController(() => text);
    }

    [Fact]
    public void 未连接且从站合法_能连不能采()
    {
        var status = Status("1");

        Assert.True(status.IsSlaveReady);
        Assert.False(status.CanAcquire);          // 链路还没连上，采不了
        Assert.True(status.CanToggleConnection);
        Assert.False(status.CanToggleAcquisition);
        Assert.True(status.CanTogglePersist);     // 从站配好了就允许配置落库开关
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("248")]
    [InlineData("abc")]
    public void 已连接但从站文本非法_只能断开(string text)
    {
        var status = Status(text);
        status.IsConnected = true;

        Assert.False(status.IsSlaveReady);
        Assert.False(status.CanAcquire);
        Assert.True(status.CanToggleConnection);   // 已经连着就必须能断开
        Assert.False(status.CanToggleAcquisition);
        Assert.False(status.CanTogglePersist);
    }

    [Fact]
    public void 采集中_三个开关都能点()
    {
        var status = Status("1");
        status.IsConnected = true;
        status.IsAcquiring = true;

        Assert.True(status.IsSlaveReady);
        Assert.True(status.CanAcquire);
        Assert.True(status.CanToggleConnection);
        Assert.True(status.CanToggleAcquisition);
        Assert.True(status.CanTogglePersist);
    }

    [Fact]
    public void 探测失败且未连接_什么都点不动()
    {
        var status = Status("1");
        status.SlaveProbeFailed = true;

        Assert.False(status.IsSlaveReady);
        Assert.False(status.CanAcquire);
        Assert.False(status.CanToggleConnection);
        Assert.False(status.CanToggleAcquisition);
        Assert.False(status.CanTogglePersist);
    }

    /// <summary>
    /// 运行中从站判失败：已经处于该状态的开关仍然要能点 —— 否则【断开】【停止采集】一起锁死，
    /// 连停都停不下来（这是原来 SyncSwitches 那套规则里最容易踩坏的一条）。
    /// </summary>
    [Fact]
    public void 采集中但从站判失败_断开与停采集仍然能点()
    {
        var status = Status("1");
        status.IsConnected = true;
        status.IsAcquiring = true;
        status.SlaveProbeFailed = true;

        Assert.False(status.IsSlaveReady);
        Assert.False(status.CanAcquire);
        Assert.True(status.CanToggleConnection);
        Assert.True(status.CanToggleAcquisition);
        Assert.True(status.CanTogglePersist);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("247", true)]
    [InlineData(" 12 ", true)]   // 两端空格无所谓
    [InlineData("0", false)]     // 0 一般作广播，不当从站
    [InlineData("248", false)]   // 上限 247
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 从站号合法性_是1到247的整数(string? text, bool valid) =>
        Assert.Equal(valid, ShellStatusController.IsValidSlaveId(text));

    [Fact]
    public void 从站号文本变了_派生属性重新发通知()
    {
        string? text = "1";
        var status = new ShellStatusController(() => text);
        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        text = "abc";
        status.NotifySlaveIdTextChanged();

        Assert.False(status.IsSlaveReady);
        Assert.Contains(nameof(ShellStatusController.IsSlaveReady), changed);
        Assert.Contains(nameof(ShellStatusController.CanAcquire), changed);
        Assert.Contains(nameof(ShellStatusController.CanToggleConnection), changed);
        Assert.Contains(nameof(ShellStatusController.CanToggleAcquisition), changed);
        Assert.Contains(nameof(ShellStatusController.CanTogglePersist), changed);
    }

    [Fact]
    public void 状态回写开关_不发用户拨动事件()
    {
        var status = Status("1");
        var toggled = new List<bool>();
        status.ConnectionSwitchToggled += toggled.Add;
        status.AcquisitionSwitchToggled += toggled.Add;

        status.IsConnected = true;
        status.IsAcquiring = true;

        Assert.True(status.ConnectionSwitch);      // 显示跟着状态走
        Assert.True(status.AcquisitionSwitch);
        Assert.Empty(toggled);                     // 但不再反过来触发连接/采集动作
    }

    [Fact]
    public void 用户拨动开关_才报事件()
    {
        var status = Status("1");
        var toggled = new List<bool>();
        status.ConnectionSwitchToggled += toggled.Add;

        status.ConnectionSwitch = true;

        Assert.Single(toggled);
        Assert.True(toggled[0]);
    }

    [Fact]
    public void 动作没生效_把开关拨回与状态一致()
    {
        var status = Status("1");
        status.ConnectionSwitch = true;    // 用户拨了开关，但动作没成功
        status.AcquisitionSwitch = true;

        status.RestoreSwitches();

        Assert.False(status.ConnectionSwitch);
        Assert.False(status.AcquisitionSwitch);
    }

    [Fact]
    public void 通信错误_联动提醒开关()
    {
        var status = Status("1");
        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(status.HasCommError);

        status.CommError = "连接失败：拒绝连接";

        Assert.True(status.HasCommError);
        Assert.Contains(nameof(ShellStatusController.HasCommError), changed);
    }
}
