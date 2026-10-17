using System.Windows;

namespace MyFences.Views;

public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>顯示輸入框；按取消回傳 null。</summary>
    public static string? Ask(string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(title, prompt, initial);
        return dialog.ShowDialog() == true ? dialog.InputBox.Text : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
