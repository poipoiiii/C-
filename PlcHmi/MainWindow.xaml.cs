using System.Windows;
using System.Windows.Input;
using PlcCore.Models;
using PlcHmi.ViewModels;

namespace PlcHmi;

public partial class MainWindow : Window
{
    /// <summary>单张卡片的最小可读宽度（DIP），用来换算一行放几张；嫌卡片太宽就调小它</summary>
    private const double MinCardWidth = 300 ;

    /// <summary>每行卡片数：由卡片区实际宽度算出来，绑给 UniformGrid.Columns</summary>
    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(MainWindow),
            new PropertyMetadata(3));

    private readonly MainViewModel _viewModel = new();

    /// <summary>机台号只在进项目时问一次，别每次重绘内容都弹</summary>
    private bool _machineNoPrompted;

    /// <summary>机台号正在就地编辑：回车/失焦提交、Esc 取消都看它</summary>
    private bool _machineNoEditing;

    /// <summary>正在弹密码框：密码框会让编辑框失焦，用它挡住那次失焦提交，免得又弹一遍</summary>
    private bool _machineNoCommitting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    /// <summary>
    /// 卡片区尺寸变了就重算一行放几张。
    /// 必须换成均分宽度的面板（UniformGrid）：WrapPanel 是按内容宽度排的，
    /// 卡片会保持自己的窄宽度，一行右边就空出一块。
    /// </summary>
    private void CardScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CardColumns = Math.Max(1, (int)(e.NewSize.Width / MinCardWidth));
    }

    /// <summary>
    /// 进项目时如果还没填机台号，问一次；填完接着弹 PLC 连接配置，把 IP/端口/从站号/周期一次填好。
    /// 机台号决定机型，空了写触发记录也认不出设备，所以开工前先把这两样配齐。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_machineNoPrompted || !_viewModel.NeedsMachineNoFix)
        {
            return;
        }

        _machineNoPrompted = true;

        var dialog = new MachineNoWindow(_viewModel.Store.Profile, _viewModel.MachineNo)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.MachineNo = dialog.MachineNo;
            OpenPlcConfigDialog();
        }
    }

    // ---------------- 机台号：点大字就地改（密码在提交时才问） ----------------

    private void MachineNoText_Click(object sender, MouseButtonEventArgs e) => BeginMachineNoEdit();

    private void BeginMachineNoEdit()
    {
        if (_machineNoEditing)
        {
            return;
        }

        _machineNoEditing = true;
        MachineNoEditor.Text = _viewModel.MachineNo;
        MachineNoText.Visibility = Visibility.Collapsed;
        MachineNoEditor.Visibility = Visibility.Visible;
        MachineNoEditor.Focus();
        MachineNoEditor.SelectAll();
    }

    private void MachineNoEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitMachineNo(stayInEditWhenInvalid: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndMachineNoEdit();   // Esc 直接放弃这次修改，原值不动
        }
    }

    private void MachineNoEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_machineNoEditing)
        {
            CommitMachineNo(stayInEditWhenInvalid: false);
        }
    }

    /// <summary>
    /// 提交就地编辑：号码没变就只退出编辑；
    /// 已有值要改（不是首次填写）先过密码，密码不过就放弃这次修改。
    /// </summary>
    /// <param name="stayInEditWhenInvalid">
    /// 号码不符合五个机型时要不要留在编辑态：按回车（true）留在原地让用户接着改，
    /// 点了别处导致失焦（false）就退回去用原值 —— 否则失焦会把人"粘"在编辑框里。
    /// </param>
    private void CommitMachineNo(bool stayInEditWhenInvalid)
    {
        if (!_machineNoEditing || _machineNoCommitting)
        {
            return;
        }

        string entered = MachineModels.Normalize(MachineNoEditor.Text);
        string current = _viewModel.MachineNo;
        bool firstFill = string.IsNullOrWhiteSpace(current);

        if (entered.Length == 0)
        {
            FinishInvalidMachineNo(stayInEditWhenInvalid, "机台号不能为空：前 4 位决定机型，写触发记录也靠它");
            return;
        }

        // 硬校验：只能是五个已知机型代号之一。认不出机型就不知道该加载哪份点位配置，
        // 所以不能像以前那样"标黄放行"。这一步放在密码前面：号码本身就不对，没必要先过密码。
        if (!MachineModels.TryResolve(entered, out _))
        {
            FinishInvalidMachineNo(stayInEditWhenInvalid, MachineModels.RequirementHint);
            return;
        }

        if (string.Equals(entered, current, StringComparison.Ordinal))
        {
            EndMachineNoEdit();
            return;
        }

        if (!firstFill)
        {
            _machineNoCommitting = true;
            try
            {
                if (new PasswordWindow { Owner = this }.ShowDialog() != true)
                {
                    EndMachineNoEdit();
                    _viewModel.StatusText = "已取消修改机台号";
                    return;
                }
            }
            finally
            {
                _machineNoCommitting = false;
            }
        }

        EndMachineNoEdit();
        _viewModel.MachineNo = entered;

        // 首次填机台号：顺手把 PLC 连接参数也配齐（带默认值）
        if (firstFill)
        {
            OpenPlcConfigDialog();
        }
    }

    /// <summary>号码不合格：留在编辑态让用户接着改，或退回原值，两处都把原因写到状态栏</summary>
    private void FinishInvalidMachineNo(bool stayInEdit, string reason)
    {
        _viewModel.StatusText = reason;

        if (stayInEdit)
        {
            MachineNoEditor.Focus();
            MachineNoEditor.SelectAll();
            return;
        }

        EndMachineNoEdit();
    }

    private void EndMachineNoEdit()
    {
        _machineNoEditing = false;
        MachineNoEditor.Visibility = Visibility.Collapsed;
        MachineNoText.Visibility = Visibility.Visible;
    }

    // ---------------- PLC 连接配置：弹窗一次填完 ----------------

    private void ConfigurePlc_Click(object sender, RoutedEventArgs e) => OpenPlcConfigDialog();

    private void OpenPlcConfigDialog() =>
        new PlcConfigWindow(_viewModel) { Owner = this }.ShowDialog();

    /// <summary>
    /// 点卡片上的曲线：弹出这个点位所属表的历史记录（按地址找覆盖它的存储组，与落库同一套判定）。
    /// 点位不在任何区段内就没有落库记录，提示去「存储映射」页调区段，不弹空窗。
    /// </summary>
    private void Chart_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagViewModel tag })
        {
            return;
        }

        var group = _viewModel.Dashboard.ResolveStorageGroup(tag.Tag);
        if (group is null)
        {
            MessageBox.Show(this,
                $"点位「{tag.Tag.Name}」（{tag.Tag.DisplayAddress}）不在任何存储组的区段内，不会落库，也就没有记录历史。\n" +
                "到「存储映射」页把区段覆盖到这个地址即可。",
                "查看记录历史", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 非模态：开着历史窗也能盯着实时曲线
        new RecordHistoryWindow(group.Name, group.TableName) { Owner = this }.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
