using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Flow.Launcher.Helper;

/// <summary>
/// Hides a scroll bar until the mouse is over its scroll viewer or the content scrolls, like the Windows 10 Start
/// menu. Themes turn it on with <c>helper:ScrollBarAutoHide.IsEnabled</c> in their ScrollBarStyle.
/// </summary>
public static class ScrollBarAutoHide
{
    private static readonly TimeSpan ShowAfterScroll = TimeSpan.FromSeconds(1);
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(150));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollBarAutoHide), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty TrackerProperty = DependencyProperty.RegisterAttached(
        "Tracker", typeof(Tracker), typeof(ScrollBarAutoHide));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar scrollBar) return;

        if ((bool)e.NewValue)
        {
            scrollBar.Loaded += OnScrollBarLoaded;
            scrollBar.Unloaded += OnScrollBarUnloaded;
            if (scrollBar.IsLoaded) Attach(scrollBar);
        }
        else
        {
            scrollBar.Loaded -= OnScrollBarLoaded;
            scrollBar.Unloaded -= OnScrollBarUnloaded;
            Detach(scrollBar);
            scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }

    private static void OnScrollBarLoaded(object sender, RoutedEventArgs e) => Attach((ScrollBar)sender);

    private static void OnScrollBarUnloaded(object sender, RoutedEventArgs e) => Detach((ScrollBar)sender);

    private static void Attach(ScrollBar scrollBar)
    {
        if (scrollBar.GetValue(TrackerProperty) != null) return;

        var scrollViewer = scrollBar.TemplatedParent as ScrollViewer ?? FindAncestor<ScrollViewer>(scrollBar);
        if (scrollViewer == null) return;

        var tracker = new Tracker(scrollBar, scrollViewer);
        scrollBar.SetValue(TrackerProperty, tracker);
        tracker.Start();
    }

    private static void Detach(ScrollBar scrollBar)
    {
        if (scrollBar.GetValue(TrackerProperty) is not Tracker tracker) return;

        tracker.Stop();
        scrollBar.ClearValue(TrackerProperty);
    }

    private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T match) return match;
        }
        return null;
    }

    private sealed class Tracker
    {
        private readonly ScrollBar _scrollBar;
        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _timer;
        private DateTime _lastScroll = DateTime.MinValue;
        private bool? _shown;

        public Tracker(ScrollBar scrollBar, ScrollViewer scrollViewer)
        {
            _scrollBar = scrollBar;
            _scrollViewer = scrollViewer;
            _timer = new DispatcherTimer(DispatcherPriority.Background, scrollBar.Dispatcher) { Interval = ShowAfterScroll };
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            _scrollViewer.MouseEnter += OnMouseChanged;
            _scrollViewer.MouseLeave += OnMouseChanged;
            _scrollViewer.ScrollChanged += OnScrollChanged;
            Update(animate: false);
        }

        public void Stop()
        {
            _timer.Stop();
            _scrollViewer.MouseEnter -= OnMouseChanged;
            _scrollViewer.MouseLeave -= OnMouseChanged;
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        }

        private void OnMouseChanged(object sender, System.Windows.Input.MouseEventArgs e) => Update(animate: true);

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Only actual scrolling counts, not the list growing or shrinking
            if (e.VerticalChange == 0) return;

            _lastScroll = DateTime.UtcNow;
            _timer.Stop();
            _timer.Start();
            Update(animate: true);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _timer.Stop();
            Update(animate: true);
        }

        private void Update(bool animate)
        {
            var show = _scrollViewer.IsMouseOver || DateTime.UtcNow - _lastScroll < ShowAfterScroll;
            if (_shown == show) return;
            _shown = show;

            var target = show ? 1.0 : 0.0;
            var animation = new DoubleAnimation(target, animate ? FadeDuration : new Duration(TimeSpan.Zero));
            _scrollBar.BeginAnimation(UIElement.OpacityProperty, animation);
        }
    }
}
