using System.Windows;
using Serilog;
using SkiaSharp;

namespace PlcHmi.Services;

/// <summary>
/// 亮/暗主题切换：整份重建 <see cref="Application.Resources"/> 的合并字典 ——
/// HC 皮肤（SkinDefault / SkinDark）+ HC 控件样式（Theme.xaml）+ 本应用调色板（PaletteLight / PaletteDark）。
///
/// 为什么整份重建而不是只换某一条：HC 控件用到的画刷是 DynamicResource 引用皮肤里的 Color，
/// 只把皮肤换掉的话，Theme.xaml 那批画刷实例不会重新生成，颜色会停在旧值上；
/// 整份换掉后界面上的 DynamicResource 会重新解析到新画刷。
/// 本应用的样式与颜色全部用 DynamicResource 引用，因此跟着一起变。
/// </summary>
public static class AppTheme
{
    private static readonly Uri SkinLight = Pack("HandyControl", "Themes/SkinDefault.xaml");
    private static readonly Uri SkinDark = Pack("HandyControl", "Themes/SkinDark.xaml");
    private static readonly Uri HandyStyles = Pack("HandyControl", "Themes/Theme.xaml");
    private static readonly Uri PaletteLight = Pack("PlcHmi", "Themes/PaletteLight.xaml");
    private static readonly Uri PaletteDark = Pack("PlcHmi", "Themes/PaletteDark.xaml");

    /// <summary>当前是不是暗色</summary>
    public static bool IsDark { get; private set; }

    /// <summary>卡片曲线的坐标轴文字色：LiveCharts 不认 WPF 资源，只能按主题给值</summary>
    public static SKColor ChartLabelColor =>
        IsDark ? new SKColor(0xA3, 0xA6, 0xAD) : new SKColor(0x90, 0x93, 0x99);

    /// <summary>卡片曲线的网格线色</summary>
    public static SKColor ChartLineColor =>
        IsDark ? new SKColor(0x3A, 0x3D, 0x43) : new SKColor(0xF0, 0xF2, 0xF5);

    public static void Apply(bool dark)
    {
        try
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Clear();
            merged.Add(Load(dark ? SkinDark : SkinLight));
            merged.Add(Load(HandyStyles));
            merged.Add(Load(dark ? PaletteDark : PaletteLight));
            IsDark = dark;
        }
        catch (Exception ex)
        {
            // 暗色皮肤加载失败（安装包不完整之类）：退回亮色，界面照常可用
            Log.Error(ex, "切换主题失败：Dark={Dark}", dark);
            if (dark)
            {
                Apply(false);
            }
        }
    }

    private static ResourceDictionary Load(Uri uri) => new() { Source = uri };

    private static Uri Pack(string assembly, string path) =>
        new($"pack://application:,,,/{assembly};component/{path}", UriKind.Absolute);
}
