using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Soundboard.App.Storage;

namespace Soundboard.App;

public partial class EditSoundDialog : Window
{
    private readonly string originalName;
    private readonly Guid? originalCategoryId;
    private readonly bool originalIsFavorite;
    private readonly SoundTileAccent originalTileAccent;
    private readonly double originalVolumePercent;
    private bool isInitialized;
    private bool isClosingWithResult;

    public EditSoundDialog(
        SoundLibraryEntry sound,
        IReadOnlyList<SoundCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(categories);
        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);

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

        originalName = sound.DisplayName;
        originalCategoryId = sound.CategoryId;
        originalIsFavorite = sound.IsFavorite;
        originalTileAccent = sound.TileAccent;
        originalVolumePercent = Math.Round(sound.VolumePercent);

        NameTextBox.Text = sound.DisplayName;
        CategoryComboBox.ItemsSource = CategoryChoices;
        CategoryComboBox.SelectedItem = CategoryChoices.First(
            choice => choice.CategoryId == sound.CategoryId);
        TileAccentComboBox.ItemsSource =
            Enum.GetValues<SoundTileAccent>();
        TileAccentComboBox.SelectedItem = sound.TileAccent;
        FavoriteCheckBox.IsChecked = sound.IsFavorite;
        VolumeSlider.Value = sound.VolumePercent;
        OriginalFileNameTextBlock.Text = sound.OriginalFileName;
        OriginalFileNameTextBlock.ToolTip = sound.OriginalFileName;
        DurationTextBlock.Text = sound.Duration.TotalHours >= 1
            ? sound.Duration.ToString(@"h\:mm\:ss")
            : sound.Duration.ToString(@"m\:ss");
        FormatTextBlock.Text = sound.FormatLabel;
        HotkeyTextBlock.Text =
            sound.Hotkey?.DisplayText ?? "No hotkey";

        isInitialized = true;
        UpdateDirtyState();

        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
        Closing += EditSoundDialog_Closing;
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

    public double VolumePercent => Math.Round(VolumeSlider.Value);

    /// <summary>
    /// True when at least one saveable property differs from the values the
    /// dialog opened with. Save stays disabled until then, and only a dirty
    /// dialog asks for confirmation when it is dismissed.
    /// </summary>
    public bool HasUnsavedChanges =>
        !string.Equals(SoundName, originalName, StringComparison.Ordinal)
        || CategoryId != originalCategoryId
        || IsFavorite != originalIsFavorite
        || TileAccent != originalTileAccent
        || Math.Abs(VolumePercent - originalVolumePercent) > 0.5d;

    private void EditField_Changed(object sender, RoutedEventArgs eventArgs)
    {
        UpdateDirtyState();
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (VolumeTextBlock is not null)
        {
            VolumeTextBlock.Text = $"{Math.Round(eventArgs.NewValue):N0}%";
        }

        UpdateDirtyState();
    }

    private void UpdateDirtyState()
    {
        if (!isInitialized || SaveButton is null)
        {
            return;
        }

        SaveButton.IsEnabled = HasUnsavedChanges && SoundName.Length > 0;
    }

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

        isClosingWithResult = true;
        DialogResult = true;
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Cancel();
    }

    /// <summary>
    /// Escape is wired manually rather than through <c>IsCancel</c> so the
    /// unsaved-changes prompt can actually keep the dialog open.
    /// </summary>
    private void EditSoundDialog_PreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Cancel();
        }
    }

    private void Cancel()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        isClosingWithResult = true;
        DialogResult = false;
    }

    private void EditSoundDialog_Closing(
        object? sender,
        CancelEventArgs eventArgs)
    {
        if (isClosingWithResult)
        {
            return;
        }

        if (!ConfirmDiscardChanges())
        {
            eventArgs.Cancel = true;
        }
    }

    private bool ConfirmDiscardChanges()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        return MessageBox.Show(
            this,
            "Discard the unsaved changes to this sound?",
            "Edit sound",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}

public sealed record SoundCategoryChoice(
    Guid? CategoryId,
    string DisplayName);
