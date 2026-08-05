namespace Soundboard.App.Presentation;

/// <summary>
/// Organization-mode selection for the sound grid.
/// </summary>
/// <remarks>
/// This is view state only: it stores sound IDs and never touches the sound
/// library. Keeping it free of WPF types also keeps the selection rules —
/// plain click, Ctrl+click, Shift+click range, select all — directly
/// testable without a window.
/// </remarks>
public sealed class LibrarySelectionState
{
    private readonly HashSet<Guid> selectedSoundIds = [];
    private Guid? anchorSoundId;

    public bool IsActive { get; private set; }

    public int Count => selectedSoundIds.Count;

    public bool HasSelection => selectedSoundIds.Count > 0;

    /// <summary>
    /// Selected sounds in the order they appear in <paramref name="orderedSoundIds"/>.
    /// Ordering the result keeps bulk commands independent of the order in
    /// which the user happened to click.
    /// </summary>
    public IReadOnlyList<Guid> InVisualOrder(
        IReadOnlyList<Guid> orderedSoundIds)
    {
        ArgumentNullException.ThrowIfNull(orderedSoundIds);
        return orderedSoundIds
            .Where(selectedSoundIds.Contains)
            .ToArray();
    }

    public bool IsSelected(Guid soundId) =>
        selectedSoundIds.Contains(soundId);

    public void Activate() => IsActive = true;

    public void Deactivate()
    {
        IsActive = false;
        Clear();
    }

    public void Clear()
    {
        selectedSoundIds.Clear();
        anchorSoundId = null;
    }

    /// <summary>
    /// Applies a click using the standard Windows list modifiers. A click
    /// without modifiers toggles, matching the checkbox affordance shown in
    /// organization mode.
    /// </summary>
    public void ApplyClick(
        IReadOnlyList<Guid> visibleSoundIds,
        Guid soundId,
        bool extend,
        bool range)
    {
        ArgumentNullException.ThrowIfNull(visibleSoundIds);
        IsActive = true;

        if (range && anchorSoundId is { } anchor)
        {
            var anchorIndex = IndexOf(visibleSoundIds, anchor);
            var targetIndex = IndexOf(visibleSoundIds, soundId);
            if (anchorIndex >= 0 && targetIndex >= 0)
            {
                var start = Math.Min(anchorIndex, targetIndex);
                var end = Math.Max(anchorIndex, targetIndex);
                if (!extend)
                {
                    selectedSoundIds.Clear();
                }

                for (var index = start; index <= end; index++)
                {
                    selectedSoundIds.Add(visibleSoundIds[index]);
                }

                return;
            }
        }

        if (!selectedSoundIds.Add(soundId))
        {
            selectedSoundIds.Remove(soundId);
        }

        anchorSoundId = soundId;
    }

    public void Select(Guid soundId)
    {
        IsActive = true;
        selectedSoundIds.Add(soundId);
        anchorSoundId = soundId;
    }

    public void SelectAll(IReadOnlyList<Guid> visibleSoundIds)
    {
        ArgumentNullException.ThrowIfNull(visibleSoundIds);
        IsActive = true;
        foreach (var soundId in visibleSoundIds)
        {
            selectedSoundIds.Add(soundId);
        }

        anchorSoundId = visibleSoundIds.Count > 0
            ? visibleSoundIds[^1]
            : null;
    }

    /// <summary>
    /// Drops sounds that no longer exist, so a removal or a filter change can
    /// never leave a command bar acting on a stale ID.
    /// </summary>
    public void Retain(IEnumerable<Guid> availableSoundIds)
    {
        ArgumentNullException.ThrowIfNull(availableSoundIds);
        var available = availableSoundIds.ToHashSet();
        selectedSoundIds.RemoveWhere(soundId => !available.Contains(soundId));
        if (anchorSoundId is { } anchor && !available.Contains(anchor))
        {
            anchorSoundId = null;
        }
    }

    public string SelectionCountText => Count == 1
        ? "1 sound selected"
        : $"{Count} sounds selected";

    private static int IndexOf(
        IReadOnlyList<Guid> soundIds,
        Guid soundId)
    {
        for (var index = 0; index < soundIds.Count; index++)
        {
            if (soundIds[index] == soundId)
            {
                return index;
            }
        }

        return -1;
    }
}
