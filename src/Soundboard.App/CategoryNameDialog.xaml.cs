using System.Windows;

namespace Soundboard.App;

public partial class CategoryNameDialog : Window
{
    public CategoryNameDialog(
        string title,
        string actionName,
        string initialName = "")
    {
        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);
        Title = title;
        PromptTextBlock.Text = actionName;
        SaveButton.Content = actionName;
        NameTextBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string CategoryName => NameTextBox.Text.Trim();

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (CategoryName.Length == 0)
        {
            MessageBox.Show(
                this,
                "Enter a non-empty category name.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
