using System.Windows;
using Soundboard.App.Storage;

namespace Soundboard.App;

public partial class EditSoundDialog : Window
{
    public EditSoundDialog(
        SoundLibraryEntry sound,
        IReadOnlyList<SoundCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(categories);
        InitializeComponent();

        CategoryChoices = new[]
        {
            new SoundCategoryChoice(null, "Uncategorized")
        }.Concat(
            categories
                .OrderBy(category => category.SortOrder)
                .Select(
                    category => new SoundCategoryChoice(
                        category.Id,
                        category.DisplayName)))
            .ToArray();

        NameTextBox.Text = sound.DisplayName;
        CategoryComboBox.ItemsSource = CategoryChoices;
        CategoryComboBox.SelectedItem = CategoryChoices.First(
            choice => choice.CategoryId == sound.CategoryId);
        TileAccentComboBox.ItemsSource =
            Enum.GetValues<SoundTileAccent>();
        TileAccentComboBox.SelectedItem = sound.TileAccent;
        FavoriteCheckBox.IsChecked = sound.IsFavorite;
        OriginalFileNameTextBlock.Text = sound.OriginalFileName;
        DurationTextBlock.Text = sound.Duration.TotalHours >= 1
            ? sound.Duration.ToString(@"h\:mm\:ss")
            : sound.Duration.ToString(@"m\:ss");
        HotkeyTextBlock.Text =
            sound.Hotkey?.DisplayText ?? "No hotkey";

        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public IReadOnlyList<SoundCategoryChoice> CategoryChoices { get; }

    public string SoundName => NameTextBox.Text.Trim();

    public Guid? CategoryId =>
        (CategoryComboBox.SelectedItem as SoundCategoryChoice)?.CategoryId;

    public bool IsFavorite => FavoriteCheckBox.IsChecked == true;

    public SoundTileAccent TileAccent =>
        TileAccentComboBox.SelectedItem is SoundTileAccent accent
            ? accent
            : SoundTileAccent.Default;

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SoundName.Length == 0)
        {
            MessageBox.Show(
                this,
                "Enter a non-empty display name.",
                "Edit sound",
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

public sealed record SoundCategoryChoice(
    Guid? CategoryId,
    string DisplayName);
