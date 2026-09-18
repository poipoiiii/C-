using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlcHmi.Features.Plc.ViewModels;
using PlcHmi.Views.Dialogs;

namespace PlcHmi.Features.Plc.Views;

/// <summary>
/// 「实时看板」页：每点位一张卡片 + 滚动曲线，按存储组分段。
/// 卡片一行放几张要看这个控件自己的实际宽度，所以 CardColumns 与尺寸变化处理都留在这里。
/// </summary>
public partial class DashboardView : UserControl
{
    /// <summary>单张卡片的最小可读宽度（DIP），用来换算一行放几张；嫌卡片太宽就调小它</summary>
    private const double MinCardWidth = 300;

    /// <summary>每行卡片数：由卡片区实际宽度算出来，绑给 UniformGrid.Columns</summary>
    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(DashboardView),
            new PropertyMetadata(3));

    public DashboardView() => InitializeComponent();

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
    /// 点卡片上的曲线：弹出这个点位所属表的历史记录（按地址找覆盖它的存储组，与落库同一套判定）。
    /// 点位不在任何区段内就没有落库记录，提示去「存储映射」页调区段，不弹空窗。
    /// </summary>
    private void Chart_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DashboardViewModel dashboard ||
            sender is not FrameworkElement { DataContext: TagViewModel tag })
        {
            return;
        }

        var group = dashboard.ResolveStorageGroup(tag.Tag);
        if (group is null)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"点位「{tag.Tag.Name}」（{tag.Tag.DisplayAddress}）不在任何存储组的区段内，不会落库，因此没有记录历史。\n" +
                "请到「存储映射」页将区段覆盖到该地址。",
                "查看记录历史", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 非模态：开着历史窗也能盯着实时曲线
        new RecordHistoryWindow(group.Name, group.TableName) { Owner = Window.GetWindow(this) }.Show();
    }
}
