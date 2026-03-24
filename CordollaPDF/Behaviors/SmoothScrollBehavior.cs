using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace CordollaPDF.Behaviors;

public static class SmoothScrollBehavior
{
    private const double DefaultDurationMs = 220;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    private static readonly DependencyProperty TargetVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "TargetVerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0d));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static double GetAnimatedVerticalOffset(DependencyObject obj) => (double)obj.GetValue(AnimatedVerticalOffsetProperty);

    private static void SetAnimatedVerticalOffset(DependencyObject obj, double value) => obj.SetValue(AnimatedVerticalOffsetProperty, value);

    private static double GetTargetVerticalOffset(DependencyObject obj) => (double)obj.GetValue(TargetVerticalOffsetProperty);

    private static void SetTargetVerticalOffset(DependencyObject obj, double value) => obj.SetValue(TargetVerticalOffsetProperty, value);

    public static void ScrollBy(ScrollViewer scrollViewer, double delta, double durationMs = DefaultDurationMs)
    {
        if (scrollViewer is null)
        {
            return;
        }

        var currentOffset = GetCurrentOffset(scrollViewer);
        var baseOffset = Math.Abs(GetTargetVerticalOffset(scrollViewer) - currentOffset) > 0.5
            ? GetTargetVerticalOffset(scrollViewer)
            : currentOffset;

        AnimateTo(
            scrollViewer,
            baseOffset + delta,
            durationMs);
    }

    public static void AnimateTo(ScrollViewer scrollViewer, double targetOffset, double durationMs = DefaultDurationMs)
    {
        if (scrollViewer is null)
        {
            return;
        }

        var clampedTarget = Math.Clamp(
            targetOffset,
            0,
            Math.Max(0, scrollViewer.ScrollableHeight));

        var currentOffset = GetCurrentOffset(scrollViewer);
        SetTargetVerticalOffset(scrollViewer, clampedTarget);
        SetAnimatedVerticalOffset(scrollViewer, currentOffset);

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = clampedTarget,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void JumpTo(ScrollViewer scrollViewer, double targetOffset)
    {
        if (scrollViewer is null)
        {
            return;
        }

        var clampedTarget = Math.Clamp(
            targetOffset,
            0,
            Math.Max(0, scrollViewer.ScrollableHeight));

        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, null);
        SetTargetVerticalOffset(scrollViewer, clampedTarget);
        SetAnimatedVerticalOffset(scrollViewer, clampedTarget);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            SetAnimatedVerticalOffset(scrollViewer, scrollViewer.VerticalOffset);
            SetTargetVerticalOffset(scrollViewer, scrollViewer.VerticalOffset);
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var delta = -e.Delta * 0.72;
        ScrollBy(scrollViewer, delta, 210);
        e.Handled = true;
    }

    private static double GetCurrentOffset(ScrollViewer scrollViewer)
    {
        var animatedOffset = GetAnimatedVerticalOffset(scrollViewer);
        return Math.Abs(animatedOffset - scrollViewer.VerticalOffset) > 0.5
            ? animatedOffset
            : scrollViewer.VerticalOffset;
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }
}
