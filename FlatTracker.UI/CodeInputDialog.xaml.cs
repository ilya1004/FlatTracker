using System.Windows;

namespace FlatTracker.UI;

public partial class CodeInputDialog : Window
{
    public CodeInputDialog(string prompt, bool isSecret)
    {
        InitializeComponent();

        PromptText.Text = prompt;
        ValueBox.MaxLength = isSecret ? 128 : 16;

        Loaded += (_, _) => ValueBox.Focus();
    }

    public string Value => ValueBox.Password;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
