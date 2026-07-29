using System.Windows;

namespace Soundboard.App;

public partial class RenameSoundDialog : Window
{
    public RenameSoundDialog(string currentName)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string SoundName => NameTextBox.Text.Trim();

    private void RenameButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SoundName.Length == 0)
        {
            MessageBox.Show(
                this,
                "Enter a non-empty display name.",
                "Rename sound",
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
