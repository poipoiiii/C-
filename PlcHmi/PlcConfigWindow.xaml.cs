using System.ComponentModel;
using System.Windows;
using PlcHmi.ViewModels;

namespace PlcHmi;

/// <summary>
/// PLC 通信配置弹窗：IP / 端口 / 从站号 / 采集周期在这里一次填完（从站号可【探测从站】自动填充）。
/// 打开时把当前值带进来当默认值并自动探一遍从站；点【取消】或直接关窗，参数还原成打开前的样子。
/// </summary>
public partial class PlcConfigWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>打开前的值：取消时用它还原（界面上改的是同一份视图模型，不还原就白改了）</summary>
    private readonly string _originalIp;
    private readonly int _originalPort;
    private readonly string _originalSlaveIdText;
    private readonly int _originalIntervalMs;

    public PlcConfigWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _originalIp = viewModel.Ip;
        _originalPort = viewModel.Port;
        _originalSlaveIdText = viewModel.SlaveIdText;
        _originalIntervalMs = viewModel.IntervalMs;

        IpBox.Text = viewModel.Ip;
        PortBox.Text = viewModel.Port.ToString();
        IntervalBox.Text = viewModel.IntervalMs.ToString();
        // SlaveIdBox 走单向绑定：探测到的从站号会自动填进来

        Loaded += (_, _) =>
        {
            IpBox.Focus();
            IpBox.SelectAll();

            // 打开就探一遍：从站号自动填好，省得现场去数
            if (_viewModel.ProbeSlavesCommand.CanExecute(null))
            {
                _viewModel.ProbeSlavesCommand.Execute(null);
            }
        };
    }

    /// <summary>确定：先校验输入，再把参数写回视图模型（写回后按需触发重连）</summary>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string ip = IpBox.Text.Trim();
        if (ip.Length == 0)
        {
            ShowError("PLC IP 不能为空");
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            ShowError("端口应为 1~65535 的整数");
            return;
        }

        if (!byte.TryParse(SlaveIdBox.Text.Trim(), out byte slaveId) || slaveId < 1 || slaveId > 247)
        {
            ShowError("从站号应为 1~247 的整数");
            return;
        }

        if (!int.TryParse(IntervalBox.Text.Trim(), out int interval) || interval < 1)
        {
            ShowError("采集周期应为正整数（毫秒）");
            return;
        }

        _viewModel.Ip = ip;
        _viewModel.Port = port;
        _viewModel.SlaveIdText = slaveId.ToString();
        _viewModel.IntervalMs = interval;

        // 参数校验过了就算"配置确认"：清掉探测失败标记，主界面的开关才放行
        _viewModel.ConfirmPlcConfig();

        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true)
        {
            _viewModel.Ip = _originalIp;
            _viewModel.Port = _originalPort;
            _viewModel.SlaveIdText = _originalSlaveIdText;
            _viewModel.IntervalMs = _originalIntervalMs;
        }

        base.OnClosing(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
