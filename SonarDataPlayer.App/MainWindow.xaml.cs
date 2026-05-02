using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SonarDataPlayer.Core;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SonarDataPlayer.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelViewModel> _channels = new();
    private readonly PlaybackState _playback = new();
    private readonly DispatcherTimer _timer;
    private readonly List<Rectangle> _timeCursors = new();
    private SonarRecording? _recording;
    private IReadOnlyDictionary<int, BitmapSource> _rawChannelImages = new Dictionary<int, BitmapSource>();
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    private bool _isUpdatingSeek;

    public MainWindow()
    {
        InitializeComponent();

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
        _rawChannelImages = BinaryWaterfallRenderer.Render(_recording);
        _channels.Clear();

        foreach (var channel in _recording.Channels)
        {
            _rawChannelImages.TryGetValue(channel.ChannelId, out var rawImage);
            var vm = new ChannelViewModel(channel, rawImage);
            vm.PropertyChanged += Channel_PropertyChanged;
            _channels.Add(vm);
        }

        var title = string.IsNullOrWhiteSpace(_recording.SourcePath)
            ? Path.GetFileNameWithoutExtension(manifestPath)
            : Path.GetFileName(_recording.SourcePath);

        RecordingTitle.Text = _rawChannelImages.Count > 0
            ? $"{title}  | raw samples"
            : $"{title}  | preview PNGs";
        EmptyViewerText.Visibility = _channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SeekSlider.Maximum = Math.Max(0, _recording.DurationSeconds);
        _playback.Seek(0, _recording.DurationSeconds);
        RenderChannels();
        UpdateReadouts();
    }

    private void Channel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.IsVisible) or nameof(ChannelViewModel.Opacity))
        {
            RenderChannels();
        }
    }

    private void ViewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RenderChannels();
        }
    }

    private void ViewerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCursorPositions();
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
        UpdateCursorPositions();

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

    private void RenderChannels()
    {
        ViewerHost.Children.Clear();
        ViewerHost.RowDefinitions.Clear();
        _timeCursors.Clear();

        if (_recording is null)
        {
            return;
        }

        var visibleChannels = _channels.Where(c => c.IsVisible && c.Image is not null).ToArray();
        EmptyViewerText.Visibility = visibleChannels.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visibleChannels.Length == 0)
        {
            return;
        }

        if (OverlayMode.IsChecked == true)
        {
            RenderOverlay(visibleChannels);
        }
        else
        {
            RenderStacked(visibleChannels);
        }

        UpdateCursorPositions();
    }

    private void RenderStacked(IReadOnlyList<ChannelViewModel> channels)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            ViewerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var panel = CreateChannelPanel(channels[i], includeLabel: true);
            panel.Margin = new Thickness(0, 0, 0, i == channels.Count - 1 ? 0 : 8);
            Grid.SetRow(panel, i);
            ViewerHost.Children.Add(panel);
        }
    }

    private void RenderOverlay(IReadOnlyList<ChannelViewModel> channels)
    {
        ViewerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var panel = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Black
        };

        foreach (var channel in channels)
        {
            panel.Children.Add(new Image
            {
                Source = channel.Image,
                Stretch = Stretch.Fill,
                Opacity = channel.Opacity
            });
        }

        var labels = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8)
        };

        foreach (var channel in channels)
        {
            labels.Children.Add(CreateLabel(channel.Label));
        }

        panel.Children.Add(labels);
        panel.Children.Add(CreateCursor());
        ViewerHost.Children.Add(panel);
    }

    private Grid CreateChannelPanel(ChannelViewModel channel, bool includeLabel)
    {
        var panel = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Black
        };

        panel.Children.Add(new Image
        {
            Source = channel.Image,
            Stretch = Stretch.Fill,
            Opacity = channel.Opacity
        });

        if (includeLabel)
        {
            panel.Children.Add(CreateLabel(channel.Label));
        }

        panel.Children.Add(CreateCursor());
        return panel;
    }

    private Border CreateLabel(string label)
    {
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(176, 0, 0, 0)),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Text = label
            }
        };
    }

    private Rectangle CreateCursor()
    {
        var cursor = new Rectangle
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new SolidColorBrush(Color.FromRgb(255, 239, 132))
        };

        _timeCursors.Add(cursor);
        return cursor;
    }

    private void UpdateCursorPositions()
    {
        if (_recording is null || _recording.DurationSeconds <= 0)
        {
            return;
        }

        var t = Math.Clamp(_playback.CurrentTimeSeconds / _recording.DurationSeconds, 0, 1);
        foreach (var cursor in _timeCursors)
        {
            if (cursor.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
            {
                continue;
            }

            cursor.Margin = new Thickness((parent.ActualWidth - cursor.Width) * t, 0, 0, 0);
        }
    }
}

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private double _opacity = 1.0;

    public ChannelViewModel(ChannelTrack channel, BitmapSource? rawImage)
    {
        Channel = channel;
        Label = $"Channel {channel.ChannelId}";
        Image = rawImage ?? LoadRotatedPreviewImage(channel.WaterfallPath);
    }

    public ChannelTrack Channel { get; }

    public string Label { get; }

    public BitmapSource? Image { get; }

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

    private static BitmapSource? LoadRotatedPreviewImage(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.Rotation = Rotation.Rotate90;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
