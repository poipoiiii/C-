using System.Windows;
using System.Windows.Input;

namespace PlcHmi.Views.Dialogs;

/// <summary>
/// 改机台号前的密码校验窗。
/// 说明白：这是**防误改**的软门槛（现场随手一改会把机型也换掉），
/// 不是安全机制 —— 客户端里的常量挡不住有心人。
/// </summary>
public partial class PasswordWindow : Window
{
    private const string EditPassword = "shjie12345";

    public PasswordWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Verify();

    /// <summary>输完直接回车就过（默认按钮也会走这里，这里显式处理一下更跟手）</summary>
    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Verify();
        }
    }

    private void Verify()
    {
        if (PasswordInput.Password != EditPassword)
        {
            ErrorText.Text = "密码错误！";
            ErrorText.Visibility = Visibility.Visible;
            PasswordInput.SelectAll();
            PasswordInput.Focus();
            return;
        }

        DialogResult = true;
    }
}
