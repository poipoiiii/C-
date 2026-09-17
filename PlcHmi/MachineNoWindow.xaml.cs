using System.Windows;

namespace PlcHmi;

/// <summary>进项目时问一次机台号的小窗：填了返回 true，跳过/直接关掉返回 false</summary>
public partial class MachineNoWindow : Window
{
    public MachineNoWindow(string profile, string current)
    {
        InitializeComponent();

        ProfileHint.Text = $"（机型「{profile}」）";
        MachineNoBox.Text = current;

        Loaded += (_, _) =>
        {
            MachineNoBox.Focus();
            MachineNoBox.SelectAll();
        };
    }

    /// <summary>用户填的机台号（已去掉两端空格）</summary>
    public string MachineNo { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        MachineNo = MachineNoBox.Text.Trim();
        DialogResult = true;
    }
}
