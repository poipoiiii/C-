using System.Windows;
using PlcHmi.Services;
using PlcHmi.ViewModels;

namespace PlcHmi;

/// <summary>
/// 「表记录历史」弹窗：从看板的卡片曲线点进来，看这张表最近写进去的记录。
/// 非模态打开，看历史的同时实时曲线还在动；服务走组合根 <see cref="AppServices.Default"/>。
/// </summary>
public partial class RecordHistoryWindow : Window
{
    private readonly TableHistoryViewModel _viewModel;

    public RecordHistoryWindow(string groupName, string tableName)
    {
        InitializeComponent();

        _viewModel = new TableHistoryViewModel(AppServices.Default, groupName, tableName);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>打开就查一次；之后的刷新交给窗口上的按钮</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.RefreshAsync();
    }
}
