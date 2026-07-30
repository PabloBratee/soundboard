using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soundboard.Audio;

namespace Soundboard.App;

public sealed class WaveformEditorControl : FrameworkElement
{
    private const double HandleHitWidth = 12d;
    private IReadOnlyList<float> peaks = Array.Empty<float>();
    private int sourceDurationMilliseconds = 100;
    private int trimStartMilliseconds;
    private int trimEndMilliseconds = 100;
    private int fadeInMilliseconds;
    private int fadeOutMilliseconds;
    private ActiveHandle activeHandle = ActiveHandle.Start;
    private bool dragging;

    // Drawing resources are resolved once and frozen. OnRender runs on
    // every drag tick, so it must not allocate brushes or pens.
    private Brush? surfaceBrush;
    private Pen? surfacePen;
    private Pen? waveformPen;
    private Brush? excludedBrush;
    private Brush? fadeBrush;
    private Brush? activeHandleBrush;
    private Brush? inactiveHandleBrush;
    private Pen? handlePen;
    private Pen? activeHandlePen;
    private Brush? placeholderBrush;

    public WaveformEditorControl()
    {
        Focusable = true;
        MinHeight = 180;
        Cursor = Cursors.Cross;
    }

    public event EventHandler? ValuesChanged;

    public int TrimStartMilliseconds => trimStartMilliseconds;

    public int TrimEndMilliseconds => trimEndMilliseconds;

    public int FadeInMilliseconds => fadeInMilliseconds;

    public int FadeOutMilliseconds => fadeOutMilliseconds;

    public int SourceDurationMilliseconds => sourceDurationMilliseconds;

    public int EffectiveDurationMilliseconds =>
        trimEndMilliseconds - trimStartMilliseconds;

    public void Configure(
        TimeSpan sourceDuration,
        int trimStart,
        int? trimEnd,
        int fadeIn,
        int fadeOut)
    {
        sourceDurationMilliseconds = Math.Max(
            100,
            checked((int)Math.Floor(sourceDuration.TotalMilliseconds)));
        trimStartMilliseconds = trimStart;
        trimEndMilliseconds = trimEnd ?? sourceDurationMilliseconds;
        fadeInMilliseconds = fadeIn;
        fadeOutMilliseconds = fadeOut;
        CoerceValues();
        InvalidateVisual();
        RaiseValuesChanged();
    }

    public void SetWaveform(WaveformData waveform)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        peaks = waveform.Peaks;
        InvalidateVisual();
    }

    public void Reset()
    {
        trimStartMilliseconds = 0;
        trimEndMilliseconds = sourceDurationMilliseconds;
        fadeInMilliseconds = 0;
        fadeOutMilliseconds = 0;
        InvalidateVisual();
        RaiseValuesChanged();
    }

    public void AdjustTrimStart(int deltaMilliseconds)
    {
        var minimumDuration = Math.Max(
            100,
            checked(fadeInMilliseconds + fadeOutMilliseconds));
        trimStartMilliseconds = Math.Clamp(
            checked(trimStartMilliseconds + deltaMilliseconds),
            0,
            trimEndMilliseconds - minimumDuration);
        InvalidateVisual();
        RaiseValuesChanged();
    }

    public void AdjustTrimEnd(int deltaMilliseconds)
    {
        var minimumDuration = Math.Max(
            100,
            checked(fadeInMilliseconds + fadeOutMilliseconds));
        trimEndMilliseconds = Math.Clamp(
            checked(trimEndMilliseconds + deltaMilliseconds),
            trimStartMilliseconds + minimumDuration,
            sourceDurationMilliseconds);
        InvalidateVisual();
        RaiseValuesChanged();
    }

    public void AdjustFadeIn(int deltaMilliseconds)
    {
        fadeInMilliseconds = Math.Clamp(
            checked(fadeInMilliseconds + deltaMilliseconds),
            0,
            EffectiveDurationMilliseconds - fadeOutMilliseconds);
        InvalidateVisual();
        RaiseValuesChanged();
    }

    public void AdjustFadeOut(int deltaMilliseconds)
    {
        fadeOutMilliseconds = Math.Clamp(
            checked(fadeOutMilliseconds + deltaMilliseconds),
            0,
            EffectiveDurationMilliseconds - fadeInMilliseconds);
        InvalidateVisual();
        RaiseValuesChanged();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        EnsureDrawingResources();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(
            surfaceBrush,
            surfacePen,
            bounds,
            8,
            8);
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var centerY = ActualHeight / 2d;
        if (peaks.Count == 0)
        {
            var text = new FormattedText(
                "Loading waveform…",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                13,
                placeholderBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(
                text,
                new Point(
                    Math.Max(8, (ActualWidth - text.Width) / 2),
                    Math.Max(8, (ActualHeight - text.Height) / 2)));
        }
        else
        {
            for (var index = 0; index < peaks.Count; index++)
            {
                var x = index * ActualWidth / Math.Max(1, peaks.Count - 1);
                var amplitude = Math.Clamp(peaks[index], 0f, 1f)
                    * (ActualHeight * 0.43);
                drawingContext.DrawLine(
                    waveformPen,
                    new Point(x, centerY - amplitude),
                    new Point(x, centerY + amplitude));
            }
        }

        var startX = TimeToX(trimStartMilliseconds);
        var endX = TimeToX(trimEndMilliseconds);
        drawingContext.DrawRectangle(
            excludedBrush,
            null,
            new Rect(0, 0, startX, ActualHeight));
        drawingContext.DrawRectangle(
            excludedBrush,
            null,
            new Rect(
                endX,
                0,
                Math.Max(0, ActualWidth - endX),
                ActualHeight));

        var fadeInEndX = TimeToX(
            trimStartMilliseconds + fadeInMilliseconds);
        var fadeOutStartX = TimeToX(
            trimEndMilliseconds - fadeOutMilliseconds);
        if (fadeInMilliseconds > 0)
        {
            drawingContext.DrawRectangle(
                fadeBrush,
                null,
                new Rect(
                    startX,
                    0,
                    Math.Max(0, fadeInEndX - startX),
                    ActualHeight));
        }

        if (fadeOutMilliseconds > 0)
        {
            drawingContext.DrawRectangle(
                fadeBrush,
                null,
                new Rect(
                    fadeOutStartX,
                    0,
                    Math.Max(0, endX - fadeOutStartX),
                    ActualHeight));
        }

        DrawHandle(drawingContext, startX, activeHandle == ActiveHandle.Start);
        DrawHandle(drawingContext, endX, activeHandle == ActiveHandle.End);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonDown(eventArgs);
        Focus();
        var x = eventArgs.GetPosition(this).X;
        activeHandle = Math.Abs(x - TimeToX(trimStartMilliseconds))
            <= Math.Abs(x - TimeToX(trimEndMilliseconds))
            ? ActiveHandle.Start
            : ActiveHandle.End;
        dragging = true;
        CaptureMouse();
        UpdateActiveHandleFromX(x);
        eventArgs.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (dragging && eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            UpdateActiveHandleFromX(eventArgs.GetPosition(this).X);
            eventArgs.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonUp(eventArgs);
        if (dragging)
        {
            dragging = false;
            ReleaseMouseCapture();
            eventArgs.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        var direction = eventArgs.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0
        };
        if (direction == 0)
        {
            if (eventArgs.Key == Key.S)
            {
                activeHandle = ActiveHandle.Start;
                InvalidateVisual();
                eventArgs.Handled = true;
            }
            else if (eventArgs.Key == Key.E)
            {
                activeHandle = ActiveHandle.End;
                InvalidateVisual();
                eventArgs.Handled = true;
            }

            return;
        }

        var increment = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? 1000
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? 10
                : 100;
        if (activeHandle == ActiveHandle.Start)
        {
            AdjustTrimStart(direction * increment);
        }
        else
        {
            AdjustTrimEnd(direction * increment);
        }

        eventArgs.Handled = true;
    }

    private void UpdateActiveHandleFromX(double x)
    {
        var milliseconds = XToTime(x);
        if (activeHandle == ActiveHandle.Start)
        {
            var delta = milliseconds - trimStartMilliseconds;
            AdjustTrimStart(delta);
        }
        else
        {
            var delta = milliseconds - trimEndMilliseconds;
            AdjustTrimEnd(delta);
        }
    }

    private void CoerceValues()
    {
        trimStartMilliseconds = Math.Clamp(
            trimStartMilliseconds,
            0,
            sourceDurationMilliseconds - 100);
        trimEndMilliseconds = Math.Clamp(
            trimEndMilliseconds,
            trimStartMilliseconds + 100,
            sourceDurationMilliseconds);
        fadeInMilliseconds = Math.Clamp(
            fadeInMilliseconds,
            0,
            EffectiveDurationMilliseconds);
        fadeOutMilliseconds = Math.Clamp(
            fadeOutMilliseconds,
            0,
            EffectiveDurationMilliseconds - fadeInMilliseconds);
    }

    /// <summary>
    /// Resolves drawing resources from the application theme once, with
    /// literal fallbacks for the designer and for any host that does not
    /// merge the Soundboard dictionaries.
    /// </summary>
    private void EnsureDrawingResources()
    {
        if (surfaceBrush is not null)
        {
            return;
        }

        surfaceBrush = ThemeBrush("TileBackgroundBrush", 0xFF1B2029);
        surfacePen = FrozenPen(
            ThemeBrush("BorderStrongBrush", 0xFF3D4658),
            1);
        waveformPen = FrozenPen(
            ThemeBrush("AccentHoverBrush", 0xFF8172F0),
            1);
        excludedBrush = FrozenBrush(0xC00F1116);
        fadeBrush = FrozenBrush(0x55E3A63F);
        activeHandleBrush = ThemeBrush("FocusBrush", 0xFFA294FF);
        inactiveHandleBrush = ThemeBrush("TextPrimaryBrush", 0xFFE8EAF0);
        handlePen = FrozenPen(
            ThemeBrush("TextPrimaryBrush", 0xFFE8EAF0),
            2);
        activeHandlePen = FrozenPen(
            ThemeBrush("FocusBrush", 0xFFA294FF),
            3);
        placeholderBrush = ThemeBrush("TextMutedBrush", 0xFF7C8698);
    }

    private Brush ThemeBrush(string resourceKey, uint fallbackArgb)
    {
        if (TryFindResource(resourceKey) is Brush found)
        {
            if (found.CanFreeze && !found.IsFrozen)
            {
                found.Freeze();
            }

            return found;
        }

        return FrozenBrush(fallbackArgb);
    }

    private static Brush FrozenBrush(uint argb)
    {
        var brush = new SolidColorBrush(
            Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private void DrawHandle(
        DrawingContext drawingContext,
        double x,
        bool active)
    {
        var brush = active ? activeHandleBrush : inactiveHandleBrush;
        var pen = active ? activeHandlePen : handlePen;
        drawingContext.DrawLine(
            pen,
            new Point(x, 0),
            new Point(x, ActualHeight));
        drawingContext.DrawRoundedRectangle(
            brush,
            pen,
            new Rect(
                x - (HandleHitWidth / 2),
                5,
                HandleHitWidth,
                28),
            3,
            3);
    }

    private double TimeToX(int milliseconds)
    {
        return milliseconds / (double)sourceDurationMilliseconds * ActualWidth;
    }

    private int XToTime(double x)
    {
        return (int)Math.Round(
            Math.Clamp(x, 0, ActualWidth)
            / Math.Max(1, ActualWidth)
            * sourceDurationMilliseconds,
            MidpointRounding.AwayFromZero);
    }

    private void RaiseValuesChanged()
    {
        ValuesChanged?.Invoke(this, EventArgs.Empty);
    }

    private enum ActiveHandle
    {
        Start,
        End
    }
}
