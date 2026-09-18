using System.Windows;

namespace PlcHmi;

/// <summary>
/// 机台号输入窗：进项目时问一次（<c>requirePassword: false</c>），
/// 以后要改则要输密码（<c>requirePassword: true</c>）—— 改机台号会连带换机型，不能让它随手被改。
/// </summary>
public partial class MachineNoWindow : Window
{
    /// <summary>
    /// 改机台号要输的密码。说明白：这是**防误改**的软门槛（现场随手改会把机型也换掉），
    /// 不是安全机制 —— 客户端里的常量挡不住有心人。
    /// </summary>
    private const string EditPassword = "shjie12345";

    private readonly bool _requirePassword;

    public MachineNoWindow(string profile, string current, bool requirePassword)
    {
        InitializeComponent();

        _requirePassword = requirePassword;

        Title = requirePassword ? "修改机台号" : "填写机台号";
        ProfileHint.Text = $"（机型「{profile}」）";
        MachineNoBox.Text = current;
        PasswordRow.Visibility = requirePassword ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            if (requirePassword)
            {
                PasswordInput.Focus();
            }
            else
            {
                MachineNoBox.Focus();
                MachineNoBox.SelectAll();
            }
        };
    }

    /// <summary>用户填的机台号（已去空格并转大写，见 MachineModels.Normalize）</summary>
    public string MachineNo { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_requirePassword && PasswordInput.Password != EditPassword)
        {
            ShowError("密码不对，改不了机台号");
            return;
        }

        MachineNo = MachineNoBox.Text.Trim();
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
