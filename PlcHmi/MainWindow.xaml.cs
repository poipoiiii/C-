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
    /// 从站号框是"失焦才提交"的，光敲字不生效容易误以为已经切过去了。
    /// 这里让回车立刻提交，改完按回车即可。
    /// </summary>
    private void SlaveIdBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ComboBox box)
        {
            box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
