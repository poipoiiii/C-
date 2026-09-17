using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlcHmi.ViewModels;

namespace PlcHmi;

public partial class MainWindow : Window
{
    /// <summary>单张卡片的最小可读宽度（DIP），用来换算一行放几张；嫌卡片太宽就调小它</summary>
    private const double MinCardWidth = 430;

    /// <summary>每行卡片数：由卡片区实际宽度算出来，绑给 UniformGrid.Columns</summary>
    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(MainWindow),
            new PropertyMetadata(3));

    private readonly MainViewModel _viewModel = new();

    /// <summary>机台号只在进项目时问一次，别每次重绘内容都弹</summary>
    private bool _machineNoPrompted;

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
    /// 从站号框、机型框都是"失焦才提交"的，光敲字不生效容易误以为已经切过去了。
    /// 这里让回车立刻提交，改完按回车即可。
    /// </summary>
    private void ComboBoxCommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ComboBox box)
        {
            box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>机台号框同样失焦才提交，回车立即生效（免得每敲一个字就写一次配置文件）</summary>
    private void MachineNoCommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 进项目时如果还没填机台号，问一次。可以跳过，但空了后面写触发记录就没法认设备，
    /// 所以宁可在开工前提醒一下；状态栏那格随时能补。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_machineNoPrompted || !_viewModel.NeedsMachineNo)
        {
            return;
        }

        _machineNoPrompted = true;

        var dialog = new MachineNoWindow(_viewModel.Store.Profile, _viewModel.MachineNo) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.MachineNo = dialog.MachineNo;
        }
    }

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
