using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using HmiCore.Models;
using PlcHmi.Services;
using SkiaSharp;

namespace PlcHmi.Features.Plc.ViewModels;

/// <summary>单个点位的实时状态 + 滚动曲线，对应界面上一张卡片</summary>
public partial class TagViewModel : ObservableObject
{
    /// <summary>曲线滑动窗口点数（1 秒一个点，即最近 60 秒）</summary>
    private const int MaxPoints = 60;

    private readonly ObservableCollection<DateTimePoint> _points = [];

    [ObservableProperty] private string _valueText = "--";
    [ObservableProperty] private string _rawText = "--";
    [ObservableProperty] private string _updatedAtText = "--";
    [ObservableProperty] private bool _isOnline;

    /// <summary>读失败的原因，正常时为空</summary>
    [ObservableProperty] private string _failureText = string.Empty;

    public TagViewModel(PlcTag tag, SKColor color)
    {
        Tag = tag;
        AccentBrush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));

        Series =
        [
            new LineSeries<DateTimePoint>
            {
                Name = tag.Name,
                Values = _points,
                GeometrySize = 0,
                LineSmoothness = 0,
                Fill = null,
                Stroke = new SolidColorPaint(color, 2)
            }
        ];

        XAxes =
        [
            new Axis
            {
                Labeler = value => new DateTime((long)value).ToString("HH:mm:ss"),
                UnitWidth = TimeSpan.FromSeconds(1).Ticks,
                TextSize = 11,
                // 轴上的颜色不属于 WPF 资源，按当前主题取；切主题时卡片会重建一遍
                LabelsPaint = new SolidColorPaint(AppTheme.ChartLabelColor),
                SeparatorsPaint = new SolidColorPaint(AppTheme.ChartLineColor, 1)
            }
        ];

        YAxes =
        [
            new Axis
            {
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(AppTheme.ChartLabelColor),
                SeparatorsPaint = new SolidColorPaint(AppTheme.ChartLineColor, 1)
            }
        ];
    }

    public PlcTag Tag { get; }

    public Brush AccentBrush { get; }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    /// <summary>把一次采集结果刷到界面上（必须在 UI 线程调用）</summary>
    public void Apply(Sample sample)
    {
        // 原始值可能是整数（16 位量）也可能是浮点（32/64 位量解出来的），统一按需显示小数
        RawText = sample.RawValue.ToString("0.###");
        // 数值和单位分开：单位由界面按小字跟在后面，读起来更清楚
        ValueText = sample.EngineeringValue.ToString("0.##");
        UpdatedAtText = sample.Timestamp.ToString("HH:mm:ss");
        IsOnline = true;
        FailureText = string.Empty;

        _points.Add(new DateTimePoint(sample.Timestamp, sample.EngineeringValue));
        while (_points.Count > MaxPoints)
        {
            _points.RemoveAt(0);
        }
    }

    public void MarkOffline()
    {
        IsOnline = false;
        UpdatedAtText = DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>本点位读失败：标灰并把原因显示在卡片上，其它点位不受影响</summary>
    public void MarkFailed(string reason)
    {
        IsOnline = false;
        ValueText = "--";
        FailureText = reason;
        UpdatedAtText = DateTime.Now.ToString("HH:mm:ss");
    }
}
