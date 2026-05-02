using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SonarDataPlayer.Core;

namespace SonarDataPlayer.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelViewModel> _channels = new();
    private readonly PlaybackState _playback = new();
    private readonly DispatcherTimer _timer;
    private SonarRecording? _recording;
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    private bool _isUpdatingSeek;

    public MainWindow()
    {
        InitializeComponent();

        ChannelList.ItemsSource = _channels;
        ChannelControls.ItemsSource = _channels;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void OpenManifest_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open processed sonar project",
            Filter = "Sonar project manifest (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadRecording(dialog.FileName);
    }

    private void LoadRecording(string manifestPath)
    {
        _recording = ProcessedProjectLoader.Load(manifestPath);
        _channels.Clear();

        foreach (var channel in _recording.Channels)
        {
            _channels.Add(new ChannelViewModel(channel));
        }

        var title = string.IsNullOrWhiteSpace(_recording.SourcePath)
            ? Path.GetFileNameWithoutExtension(manifestPath)
            : Path.GetFileName(_recording.SourcePath);

        RecordingTitle.Text = title;
        EmptyViewerText.Visibility = _channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SeekSlider.Maximum = Math.Max(0, _recording.DurationSeconds);
        _playback.Seek(0, _recording.DurationSeconds);
        UpdateReadouts();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        _playback.Toggle();
        PlayPauseButton.Content = _playback.IsPlaying ? "Pause" : "Play";
        _lastTick = DateTimeOffset.Now;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_recording is null || _isUpdatingSeek)
        {
            return;
        }

        _playback.Seek(e.NewValue, _recording.DurationSeconds);
        UpdateReadouts();
    }

    private void RateSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RateSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            double.TryParse(tag, out var rate))
        {
            _playback.SetRate(rate);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (_recording is null)
        {
            return;
        }

        _playback.Advance(elapsed, _recording.DurationSeconds);
        PlayPauseButton.Content = _playback.IsPlaying ? "Pause" : "Play";
        UpdateReadouts();
    }

    private void UpdateReadouts()
    {
        if (_recording is null)
        {
            return;
        }

        _isUpdatingSeek = true;
        SeekSlider.Value = _playback.CurrentTimeSeconds;
        _isUpdatingSeek = false;

        TimeReadout.Text = $"{_playback.CurrentTimeSeconds:0.0} / {_recording.DurationSeconds:0.0} s";

        var ping = _recording.FindNearestTelemetry(_playback.CurrentTimeSeconds);
        if (ping is null)
        {
            return;
        }

        DepthReadout.Text = $"Depth: {Format(ping.DepthMeters, "0.0 m")}";
        RangeReadout.Text = $"Range: {Format(ping.MinimumRangeMeters, "0.0")} - {Format(ping.MaximumRangeMeters, "0.0 m")}";
        PositionReadout.Text = $"Position: {Format(ping.Latitude, "0.000000")}, {Format(ping.Longitude, "0.000000")}";
        SpeedReadout.Text = $"Speed: {Format(ping.SpeedMetersPerSecond, "0.0 m/s")}";
        HeadingReadout.Text = $"Heading: {Format(ping.HeadingDegrees, "0")} deg";
        TempReadout.Text = $"Temp: {Format(ping.TemperatureCelsius, "0.0 C")}";
        PingReadout.Text = $"Ping: {ping.RecordNumber}  Ch: {ping.ChannelId}  Samples: {ping.SampleCount}";
    }

    private static string Format(double? value, string format)
    {
        return value.HasValue ? value.Value.ToString(format) : "-";
    }
}

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private double _opacity = 1.0;

    public ChannelViewModel(ChannelTrack channel)
    {
        Channel = channel;
        Label = $"Channel {channel.ChannelId}";
        Image = File.Exists(channel.WaterfallPath)
            ? new BitmapImage(new Uri(channel.WaterfallPath))
            : null;
    }

    public ChannelTrack Channel { get; }

    public string Label { get; }

    public BitmapImage? Image { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Opacity));
        }
    }

    public double Opacity
    {
        get => IsVisible ? _opacity : 0;
        set
        {
            var next = Math.Clamp(value, 0, 1);
            if (Math.Abs(_opacity - next) < 0.001)
            {
                return;
            }

            _opacity = next;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
