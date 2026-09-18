using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlcHmi.Controls;

/// <summary>
/// 分区标题三件套：强调竖条 + 标题 + 灰提示。
/// 之前每处都手写 Rectangle + SectionTitle + SectionHint，样式容易走样，抽成一个控件；
/// 模板是 App.xaml 里的隐式样式。
/// 竖条后面要跟输入框/按钮时，把本控件放进原有的 WrapPanel 当第一个子元素即可，那些元素照旧。
/// </summary>
public class SectionHeader : Control
{
    /// <summary>竖条默认色 = 主色（Accent 在亮/暗两套里都是同一支蓝，所以这里可以直接给常量）</summary>
    private static readonly Brush DefaultAccent = CreateDefaultAccent();

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty));

    /// <summary>标题后面的灰字说明；留空整段收起，不留多余空隙</summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty));

    /// <summary>竖条颜色，默认主色；成功/警告一类的分区传对应语义色</summary>
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SectionHeader),
            new PropertyMetadata(DefaultAccent));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static Brush CreateDefaultAccent()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x40, 0x9E, 0xFF));
        brush.Freeze();
        return brush;
    }
}
