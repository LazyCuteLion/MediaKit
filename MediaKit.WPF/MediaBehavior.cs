using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MediaKit.WPF;

/// <summary>
/// 为 MediaElement 提供可绑定的进度附加属性。
/// 设置 MediaBehavior.Interval="100" 即可启用，设为 0 禁用。
/// </summary>
public static class MediaBehavior
{
    private static readonly Dictionary<MediaElement, ProgressTracker> _trackers = new();

    #region Attached Properties

    /// <summary>
    /// 轮询间隔（毫秒）。大于 0 启用进度追踪，0 或负数禁用。
    /// </summary>
    public static readonly DependencyProperty IntervalProperty =
        DependencyProperty.RegisterAttached("Interval", typeof(int), typeof(MediaBehavior),
            new PropertyMetadata(0, OnIntervalChanged));

    /// <summary>
    /// 当前播放进度（0~100）。支持双向绑定，外部设置时自动 Seek。
    /// </summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.RegisterAttached("Progress", typeof(double), typeof(MediaBehavior),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnProgressChanged));

    /// <summary>
    /// 当前播放位置（只读）。
    /// </summary>
    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.RegisterAttached("Position", typeof(TimeSpan), typeof(MediaBehavior),
            new PropertyMetadata(TimeSpan.Zero));

    /// <summary>
    /// 媒体总时长（只读）。
    /// </summary>
    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.RegisterAttached("Duration", typeof(TimeSpan), typeof(MediaBehavior),
            new PropertyMetadata(TimeSpan.Zero));

    public static int GetInterval(UIElement e) => (int)e.GetValue(IntervalProperty);
    public static void SetInterval(UIElement e, int v) => e.SetValue(IntervalProperty, v);

    public static double GetProgress(UIElement e) => (double)e.GetValue(ProgressProperty);
    public static void SetProgress(UIElement e, double v) => e.SetValue(ProgressProperty, v);

    public static TimeSpan GetPosition(UIElement e) => (TimeSpan)e.GetValue(PositionProperty);
    public static void SetPosition(UIElement e, TimeSpan v) => e.SetValue(PositionProperty, v);

    public static TimeSpan GetDuration(UIElement e) => (TimeSpan)e.GetValue(DurationProperty);
    public static void SetDuration(UIElement e, TimeSpan v) => e.SetValue(DurationProperty, v);

    #endregion

    #region Callbacks

    private static void OnIntervalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MediaElement me) return;

        var ms = (int)e.NewValue;
        if (ms > 0)
        {
            if (_trackers.TryGetValue(me, out var existing))
            {
                existing.UpdateInterval(ms);
            }
            else
            {
                // 订阅卸载/加载，卸载时清除追踪器避免 _trackers 静态字典泄漏，重载时按需重建
                me.Unloaded -= OnMediaUnloaded;
                me.Unloaded += OnMediaUnloaded;
                me.Loaded -= OnMediaLoaded;
                me.Loaded += OnMediaLoaded;

                var tracker = new ProgressTracker(me, ms);
                _trackers[me] = tracker;
                tracker.Start();
            }
        }
        else
        {
            me.Unloaded -= OnMediaUnloaded;
            me.Loaded -= OnMediaLoaded;
            if (_trackers.TryGetValue(me, out var tracker))
            {
                tracker.Stop();
                _trackers.Remove(me);
            }
        }
    }

    private static void OnMediaUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement me && _trackers.TryGetValue(me, out var tracker))
        {
            tracker.Stop();
            _trackers.Remove(me);
        }
    }

    private static void OnMediaLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement me) return;
        var ms = GetInterval(me);
        if (ms > 0 && !_trackers.ContainsKey(me))
        {
            var tracker = new ProgressTracker(me, ms);
            _trackers[me] = tracker;
            tracker.Start();
        }
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaElement me && _trackers.TryGetValue(me, out var tracker))
            tracker.SeekByProgress((double)e.NewValue);
    }

    #endregion

    #region ProgressTracker

    private sealed class ProgressTracker
    {
        private readonly MediaElement _media;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _seekDebounce;
        private bool _isSyncing;
        private bool _isSeeking;
        private double _pendingProgress;

        public ProgressTracker(MediaElement media, int intervalMs)
        {
            _media = media;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
            _timer.Tick += OnTick;

            _seekDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _seekDebounce.Tick += OnSeekDebounce;
        }

        public void UpdateInterval(int ms)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(ms);
        }

        public void Start()
        {
            _media.MediaOpened += OnMediaOpened;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _seekDebounce.Stop();
            _media.MediaOpened -= OnMediaOpened;
        }

        public void SeekByProgress(double progress)
        {
            if (_isSyncing) return;

            // 进入拖拽状态，暂停定时器更新，节流 Seek
            _isSeeking = true;
            _pendingProgress = progress;
            _seekDebounce.Stop();
            _seekDebounce.Start();
        }

        private void OnSeekDebounce(object? sender, EventArgs e)
        {
            _seekDebounce.Stop();

            var duration = _media.NaturalDuration;
            if (!duration.HasTimeSpan) { _isSeeking = false; return; }

            var clamped = Math.Max(0, Math.Min(100, _pendingProgress));
            var pos = TimeSpan.FromTicks((long)(duration.TimeSpan.Ticks * clamped / 100.0));
            _media.Position = pos;

            _isSyncing = true;
            SetPosition(_media, pos);
            _isSyncing = false;

            _isSeeking = false;
        }

        private void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            var duration = _media.NaturalDuration;
            if (duration.HasTimeSpan)
                SetDuration(_media, duration.TimeSpan);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            // 拖拽期间不更新，避免进度条回跳
            if (_isSeeking) return;

            var duration = _media.NaturalDuration;
            if (!duration.HasTimeSpan || duration.TimeSpan.Ticks <= 0) return;

            _isSyncing = true;
            var pos = _media.Position;
            SetPosition(_media, pos);
            SetProgress(_media, (double)pos.Ticks / duration.TimeSpan.Ticks * 100.0);
            SetDuration(_media, duration.TimeSpan);
            _isSyncing = false;
        }
    }

    #endregion
}
