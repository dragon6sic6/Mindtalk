using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Mindtalk.App.Meetings;

/// <summary>
/// The meeting's sound, when it's kept: play/pause, a scrubber, the time and the speed — a
/// capsule above the ask bar that appears once you play (or click a time).
/// </summary>
internal sealed partial class AudioBar : Grid
{
    private static readonly double[] Speeds = [1, 1.25, 1.5, 2, 0.75];

    private MediaPlayer? _player;
    private string? _path;
    private double _duration;
    private readonly FontIcon _playIcon = UI.Icon(Icons.Play, 13);
    private readonly Slider _scrubber;
    private readonly TextBlock _time = UI.Text("0:00", 12.5, FontWeights.Medium, "MutedBrush");
    private readonly TextBlock _speedText = UI.Text("1×", 12.5, FontWeights.SemiBold);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _seeking;
    private int _speed;

    public AudioBar()
    {
        Height = 46;
        Visibility = Visibility.Collapsed;
        Parts.Tabular(_time);
        var face = new Border { CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 12, 0) };
        UI.Bind(face, Border.BackgroundProperty, "FieldBrush");
        UI.Bind(face, Border.BorderBrushProperty, "FieldStrokeBrush");
        var row = new Grid { ColumnSpacing = 10 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var playFace = new Grid { Width = 32, Height = 32 };
        var disc = new Microsoft.UI.Xaml.Shapes.Ellipse();
        UI.Bind(disc, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "ChipBrush");
        playFace.Children.Add(disc);
        playFace.Children.Add(_playIcon);
        var play = UI.PlainButton(playFace, Toggle, Loc.T("Spela upp eller pausa", "Play or pause"));
        row.Children.Add(play);

        _scrubber = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.1, VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false };
        _scrubber.Resources["SliderTrackValueFill"] = UI.Brush("InkBrush");
        _scrubber.Resources["SliderTrackValueFillPointerOver"] = UI.Brush("InkBrush");
        _scrubber.Resources["SliderTrackValueFillPressed"] = UI.Brush("InkBrush");
        _scrubber.Resources["SliderThumbBackground"] = UI.Brush("InkBrush");
        _scrubber.Resources["SliderThumbBackgroundPointerOver"] = UI.Brush("InkBrush");
        _scrubber.Resources["SliderThumbBackgroundPressed"] = UI.Brush("InkBrush");
        AutomationProperties.SetName(_scrubber, Loc.T("Position", "Position"));
        _scrubber.ValueChanged += (_, e) =>
        {
            if (_seeking || _player is null) return;
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
            ShowTime(e.NewValue);
        };
        Grid.SetColumn(_scrubber, 1);
        row.Children.Add(_scrubber);
        Grid.SetColumn(_time, 2);
        row.Children.Add(_time);

        var speed = UI.PlainButton(_speedText, NextSpeed, Loc.T("Hastighet", "Speed"));
        speed.MinWidth = 34;
        speed.HorizontalContentAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(speed, 3);
        row.Children.Add(speed);
        var close = UI.PlainButton(UI.Icon(Icons.XMark, 10, "MutedBrush"), () => { Pause(); Visibility = Visibility.Collapsed; }, Loc.T("Dölj spelaren", "Hide the player"));
        Grid.SetColumn(close, 4);
        row.Children.Add(close);
        face.Child = row;
        Children.Add(SoftShadow.Around(face, 23, 14, 3, UI.Dark ? 0.5 : 0.10));

        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();
    }

    public bool HasAudio => _path is not null;

    /// The meeting's audio file (null: none kept, the bar stays hidden).
    public void Load(string? path, double duration)
    {
        _duration = duration;
        if (path == _path) return;
        Close();
        _path = path;
        if (path is null) { Visibility = Visibility.Collapsed; return; }
        _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), AutoPlay = false };
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(Tick);
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(Tick);
        _scrubber.Maximum = Math.Max(1, duration);
#if DEBUG
        if (Demo.Has("--demo-player")) Visibility = Visibility.Visible;
#endif
    }

    public void PlayFrom(double seconds)
    {
        if (_player is null) return;
        Visibility = Visibility.Visible;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds - 1));
        _player.Play();
        _timer.Start();
        Tick();
    }

    public void Seek(double seconds)
    {
        if (_player is null) return;
        Visibility = Visibility.Visible;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
        Tick();
    }

    private void Toggle()
    {
        if (_player is null) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) Pause();
        else { _player.Play(); _timer.Start(); }
        Tick();
    }

    private void Pause() => _player?.Pause();

    private void NextSpeed()
    {
        _speed = (_speed + 1) % Speeds.Length;
        if (_player is not null) _player.PlaybackSession.PlaybackRate = Speeds[_speed];
        _speedText.Text = $"{Speeds[_speed].ToString("0.##", Loc.Culture)}×";
    }

    private void Tick()
    {
        if (_player is null) return;
        var session = _player.PlaybackSession;
        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        _playIcon.Glyph = playing ? Icons.Pause : Icons.Play;
        var total = session.NaturalDuration.TotalSeconds > 0 ? session.NaturalDuration.TotalSeconds : _duration;
        _seeking = true;
        _scrubber.Maximum = Math.Max(1, total);
        _scrubber.Value = Math.Min(total, session.Position.TotalSeconds);
        _seeking = false;
        ShowTime(session.Position.TotalSeconds, total);
        if (!playing) _timer.Stop();
    }

    private void ShowTime(double at, double? total = null) =>
        _time.Text = $"{Meeting.Clock(at)} / {Meeting.Clock(total ?? _scrubber.Maximum)}";

    public void Close()
    {
        _timer.Stop();
        if (_player is null) return;
        _player.Pause();
        _player.Dispose();
        _player = null;
        _path = null;
    }
}
