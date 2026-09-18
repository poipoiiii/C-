using System.Windows;
using HmiCore.Models;

namespace PlcHmi.Views.Dialogs;

/// <summary>
/// 机台号输入窗：进项目时问一次（机台号为空时）。
/// 以后再改不用开这个窗 —— 直接点主界面左上角的大字就地改，改动仍要过 <see cref="PasswordWindow"/>。
/// </summary>
public partial class MachineNoWindow : Window
{
    public MachineNoWindow(string profile, string current)
    {
        InitializeComponent();

        Header.Hint = $"（机型「{profile}」）";
        MachineNoBox.Text = current;

        Loaded += (_, _) =>
        {
            MachineNoBox.Focus();
            MachineNoBox.SelectAll();
        };
    }

    /// <summary>用户填的机台号（已去空格并转大写，见 MachineModels.Normalize）</summary>
    public string MachineNo { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string machineNo = MachineNoBox.Text.Trim();
        if (machineNo.Length == 0)
        {
            ShowError("机台号不能为空：前 4 位决定机型，写触发记录也靠它");
            return;
        }

        // 硬校验：前 4 位必须是五个已知机型代号之一，认不出来就不放行 ——
        // 认不出机型就等于不知道该加载哪份点位配置，宁可让用户当场改对
        if (!MachineModels.TryResolve(machineNo, out _))
        {
            ShowError(MachineModels.RequirementHint);
            return;
        }

        MachineNo = MachineModels.Normalize(machineNo);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
