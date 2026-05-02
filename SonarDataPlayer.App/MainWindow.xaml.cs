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
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SonarDataPlayer.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelViewModel> _channels = new();
    private readonly PlaybackState _playback = new();
    private readonly DispatcherTimer _timer;
    private readonly List<Image> _sonarImages = new();
    private readonly List<Rectangle> _timeCursors = new();
    private readonly List<TextBlock> _cursorTimeLabels = new();
    private TextBlock? _viewerDepthReadout;
    private TextBlock? _viewerTempReadout;
    private SonarRecording? _recording;
    private IReadOnlyDictionary<int, BitmapSource> _rawChannelImages = new Dictionary<int, BitmapSource>();
    private double? _manualMaxDepthMeters;
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    private bool _isUpdatingSeek;
    private DepthUnit _depthUnit = DepthUnit.Meters;
    private SpeedUnit _speedUnit = SpeedUnit.Mph;
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;
    private double _zoomWindowSeconds;
    private int _utcOffsetHours = -4;

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
        _manualMaxDepthMeters = null;
        RenderRawChannelImages();
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
        UpdateImageViewports();
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

    private void UnitSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _depthUnit = SelectedDepthUnit();
        _speedUnit = SelectedSpeedUnit();
        _temperatureUnit = SelectedTemperatureUnit();
        _utcOffsetHours = SelectedUtcOffsetHours();
        RenderChannels();
        UpdateReadouts();
    }

    private void ZoomSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _zoomWindowSeconds = SelectedZoomWindowSeconds();
        UpdateImageViewports();
        UpdateCursorPositions();
    }

    private void DepthZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        var current = GetDisplayMaxRangeMeters();
        _manualMaxDepthMeters = Math.Max(3.0, current * 0.8);
        RebuildDepthScaledView();
    }

    private void DepthZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        var auto = GetAutoMaxRangeMeters();
        var current = GetDisplayMaxRangeMeters();
        _manualMaxDepthMeters = Math.Min(auto, current * 1.25);
        RebuildDepthScaledView();
    }

    private void DepthZoomAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        _manualMaxDepthMeters = null;
        RebuildDepthScaledView();
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
        UpdateImageViewports();
        UpdateCursorPositions();

        var ping = _recording.FindNearestTelemetry(_playback.CurrentTimeSeconds);
        if (ping is null)
        {
            return;
        }

        DepthReadout.Text = $"Depth: {FormatDepth(ping.DepthMeters)}";
        RangeReadout.Text = $"Range: {FormatDepth(ping.MinimumRangeMeters)} - {FormatDepth(ping.MaximumRangeMeters)}";
        PositionReadout.Text = $"Position: {Format(ping.Latitude, "0.000000")}, {Format(ping.Longitude, "0.000000")}";
        SpeedReadout.Text = $"Speed: {FormatSpeed(ping.SpeedMetersPerSecond)}";
        HeadingReadout.Text = $"Heading: {Format(ping.HeadingDegrees, "0")} deg";
        TempReadout.Text = $"Water Temp: {FormatTemperature(ping.TemperatureCelsius)}";
        PingReadout.Text = $"Ping: {ping.RecordNumber}  Ch: {ping.ChannelId}  Samples: {ping.SampleCount}";
        UpdateViewerTelemetry(ping);
    }

    private static string Format(double? value, string format)
    {
        return value.HasValue ? value.Value.ToString(format) : "-";
    }

    private string FormatDepth(double? meters)
    {
        if (!meters.HasValue)
        {
            return "-";
        }

        var (value, suffix) = _depthUnit switch
        {
            DepthUnit.Feet => (meters.Value * 3.280839895, "ft"),
            DepthUnit.Fathoms => (meters.Value / 1.8288, "fm"),
            _ => (meters.Value, "m")
        };

        return $"{value:0.0} {suffix}";
    }

    private string FormatSpeed(double? metersPerSecond)
    {
        if (!metersPerSecond.HasValue)
        {
            return "-";
        }

        var (value, suffix) = _speedUnit switch
        {
            SpeedUnit.Knots => (metersPerSecond.Value * 1.943844492, "kt"),
            _ => (metersPerSecond.Value * 2.236936292, "mph")
        };

        return $"{value:0.0} {suffix}";
    }

    private string FormatTemperature(double? celsius)
    {
        if (!celsius.HasValue)
        {
            return "-";
        }

        var (value, suffix) = _temperatureUnit switch
        {
            TemperatureUnit.Fahrenheit => ((celsius.Value * 9.0 / 5.0) + 32.0, "F"),
            _ => (celsius.Value, "C")
        };

        return $"{value:0.0} {suffix}";
    }

    private string FormatLocalTime(DateTime? utc)
    {
        if (!utc.HasValue)
        {
            return "-";
        }

        var local = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).AddHours(_utcOffsetHours);
        return $"{local:yyyy-MM-dd HH:mm:ss} UTC{_utcOffsetHours:+0;-0;+0}";
    }

    private string FormatCursorLocalTime()
    {
        if (_recording?.FindNearestTelemetry(_playback.CurrentTimeSeconds)?.TimestampUtc is not { } utc)
        {
            return string.Empty;
        }

        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(_utcOffsetHours);
        return local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private DepthUnit SelectedDepthUnit()
    {
        return DepthUnitSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               Enum.TryParse<DepthUnit>(tag, out var unit)
            ? unit
            : DepthUnit.Meters;
    }

    private SpeedUnit SelectedSpeedUnit()
    {
        return SpeedUnitSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               Enum.TryParse<SpeedUnit>(tag, out var unit)
            ? unit
            : SpeedUnit.Mph;
    }

    private TemperatureUnit SelectedTemperatureUnit()
    {
        return TemperatureUnitSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               Enum.TryParse<TemperatureUnit>(tag, out var unit)
            ? unit
            : TemperatureUnit.Celsius;
    }

    private int SelectedUtcOffsetHours()
    {
        return UtcOffsetSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               int.TryParse(tag, out var offset)
            ? offset
            : -4;
    }

    private double SelectedZoomWindowSeconds()
    {
        return ZoomSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               double.TryParse(tag, out var seconds)
            ? seconds
            : 0;
    }

    private void RenderChannels()
    {
        ViewerHost.Children.Clear();
        ViewerHost.RowDefinitions.Clear();
        _sonarImages.Clear();
        _timeCursors.Clear();
        _cursorTimeLabels.Clear();
        _viewerDepthReadout = null;
        _viewerTempReadout = null;

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

        AddViewerTelemetryOverlay();
        UpdateCursorPositions();
    }

    private void RebuildDepthScaledView()
    {
        RenderRawChannelImages();
        foreach (var channel in _channels)
        {
            _rawChannelImages.TryGetValue(channel.Channel.ChannelId, out var rawImage);
            channel.SetImage(rawImage ?? ChannelViewModel.LoadRotatedPreviewImage(channel.Channel.WaterfallPath));
        }

        RenderChannels();
        UpdateReadouts();
    }

    private void RenderRawChannelImages()
    {
        _rawChannelImages = _recording is null
            ? new Dictionary<int, BitmapSource>()
            : BinaryWaterfallRenderer.Render(_recording, _manualMaxDepthMeters);
    }

    private void RenderStacked(IReadOnlyList<ChannelViewModel> channels)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            ViewerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var panel = CreateChannelPanel(channels[i]);
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
            panel.Children.Add(CreateSonarImage(channel));
        }

        panel.Children.Add(CreateDepthGrid(null));

        panel.Children.Add(CreateCursor());
        panel.Children.Add(CreateCursorTimeLabel());
        ViewerHost.Children.Add(panel);
    }

    private Grid CreateChannelPanel(ChannelViewModel channel)
    {
        var panel = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Black
        };

        panel.Children.Add(CreateSonarImage(channel));

        panel.Children.Add(CreateDepthGrid(channel.Channel.ChannelId));

        panel.Children.Add(CreateCursor());
        panel.Children.Add(CreateCursorTimeLabel());
        return panel;
    }

    private void AddViewerTelemetryOverlay()
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(198, 255, 255, 244)),
            Padding = new Thickness(10, 6, 12, 8),
            Margin = new Thickness(10),
            Child = new StackPanel
            {
                Children =
                {
                    (_viewerDepthReadout = new TextBlock
                    {
                        Foreground = Brushes.Black,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 46,
                        FontWeight = FontWeights.Bold,
                        LineHeight = 44,
                        Text = "--.-"
                    }),
                    (_viewerTempReadout = new TextBlock
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 18,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(1, 0, 0, 0),
                        Text = "--.-"
                    })
                }
            }
        };

        Grid.SetRowSpan(border, Math.Max(1, ViewerHost.RowDefinitions.Count));
        Panel.SetZIndex(border, 20);
        ViewerHost.Children.Add(border);
        UpdateViewerTelemetry(_recording?.FindNearestTelemetry(_playback.CurrentTimeSeconds));
    }

    private void UpdateViewerTelemetry(PingTelemetry? ping)
    {
        if (_viewerDepthReadout is null || _viewerTempReadout is null)
        {
            return;
        }

        _viewerDepthReadout.Text = FormatDigitalDepth(ping?.DepthMeters);
        _viewerTempReadout.Text = FormatTemperature(ping?.TemperatureCelsius);
    }

    private string FormatDigitalDepth(double? meters)
    {
        if (!meters.HasValue)
        {
            return "--.-";
        }

        var (value, suffix) = _depthUnit switch
        {
            DepthUnit.Feet => (meters.Value * 3.280839895, "ft"),
            DepthUnit.Fathoms => (meters.Value / 1.8288, "fm"),
            _ => (meters.Value, "m")
        };

        return $"{value:0.0}{suffix}";
    }

    private Image CreateSonarImage(ChannelViewModel channel)
    {
        var image = new Image
        {
            Source = channel.Image,
            Stretch = Stretch.Fill,
            Opacity = channel.Opacity,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _sonarImages.Add(image);
        return image;
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

    private TextBlock CreateCursorTimeLabel()
    {
        var label = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = Brushes.Black,
            Background = new SolidColorBrush(Color.FromRgb(255, 239, 132)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 0, 4)
        };

        _cursorTimeLabels.Add(label);
        return label;
    }

    private Canvas CreateDepthGrid(int? channelId)
    {
        var canvas = new Canvas
        {
            IsHitTestVisible = false,
            Tag = channelId
        };
        canvas.SizeChanged += DepthGrid_SizeChanged;
        return canvas;
    }

    private void DepthGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            DrawDepthGrid(canvas);
        }
    }

    private void DrawDepthGrid(Canvas canvas)
    {
        canvas.Children.Clear();
        if (_recording is null || canvas.ActualHeight <= 0 || canvas.ActualWidth <= 0)
        {
            return;
        }

        var channelId = canvas.Tag as int?;
        var maxDepthMeters = GetDisplayMaxRangeMeters();
        if (maxDepthMeters <= 0)
        {
            return;
        }

        var (intervalMeters, suffix) = _depthUnit switch
        {
            DepthUnit.Feet => (10.0 / 3.280839895, "ft"),
            DepthUnit.Fathoms => (1.8288, "fm"),
            _ => (3.0, "m")
        };

        var intervalDisplay = _depthUnit switch
        {
            DepthUnit.Feet => 10.0,
            DepthUnit.Fathoms => 1.0,
            _ => 3.0
        };

        var stroke = new SolidColorBrush(Color.FromArgb(92, 255, 255, 255));
        var textBrush = new SolidColorBrush(Color.FromArgb(210, 238, 242, 247));

        for (var depthMeters = intervalMeters; depthMeters < maxDepthMeters; depthMeters += intervalMeters)
        {
            var y = (depthMeters / maxDepthMeters) * canvas.ActualHeight;
            var line = new Line
            {
                X1 = 0,
                X2 = canvas.ActualWidth,
                Y1 = y,
                Y2 = y,
                Stroke = stroke,
                StrokeThickness = 1
            };
            canvas.Children.Add(line);

            var displayValue = (depthMeters / intervalMeters) * intervalDisplay;
            var label = new TextBlock
            {
                Text = $"{displayValue:0} {suffix}",
                Foreground = textBrush,
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                FontSize = 11,
                Padding = new Thickness(4, 1, 4, 1)
            };
            Canvas.SetLeft(label, 6);
            Canvas.SetTop(label, Math.Max(0, y - 10));
            canvas.Children.Add(label);
        }

        DrawBottomTrace(canvas, channelId, maxDepthMeters);
    }

    private void DrawBottomTrace(Canvas canvas, int? channelId, double maxDepthMeters)
    {
        if (_recording is null || channelId is null || _recording.Frames.Count < 2)
        {
            return;
        }

        var points = new PointCollection();
        for (var i = 0; i < _recording.Frames.Count; i++)
        {
            var block = _recording.Frames[i].Channels.FirstOrDefault(c => c.ChannelId == channelId.Value);
            if (block?.BottomDepthMeters is not { } bottom || bottom <= 0)
            {
                continue;
            }

            var x = (_recording.Frames.Count <= 1 ? 0 : i / (double)(_recording.Frames.Count - 1)) * canvas.ActualWidth;
            var y = Math.Clamp((bottom / maxDepthMeters) * canvas.ActualHeight, 0, canvas.ActualHeight);
            points.Add(new Point(x, y));
        }

        if (points.Count < 2)
        {
            return;
        }

        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromArgb(210, 255, 210, 86)),
            StrokeThickness = 1.5
        });
    }

    private double GetDisplayMaxRangeMeters()
    {
        return _manualMaxDepthMeters ?? GetAutoMaxRangeMeters();
    }

    private double GetAutoMaxRangeMeters()
    {
        if (_recording is null)
        {
            return 0;
        }

        return _recording.Frames
            .SelectMany(frame => frame.Channels)
            .Select(channel => channel.MaximumRangeMeters ?? 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private void UpdateCursorPositions()
    {
        if (_recording is null || _recording.DurationSeconds <= 0)
        {
            return;
        }

        var (visibleStart, visibleDuration) = GetVisibleTimeWindow();
        var t = visibleDuration <= 0
            ? 0
            : Math.Clamp((_playback.CurrentTimeSeconds - visibleStart) / visibleDuration, 0, 1);
        foreach (var cursor in _timeCursors)
        {
            if (cursor.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
            {
                continue;
            }

            cursor.Margin = new Thickness((parent.ActualWidth - cursor.Width) * t, 0, 0, 0);
        }

        var labelText = FormatCursorLocalTime();
        foreach (var label in _cursorTimeLabels)
        {
            if (label.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
            {
                continue;
            }

            label.Text = labelText;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = (parent.ActualWidth - label.DesiredSize.Width) / 2.0;
            var maxLeft = Math.Max(0, parent.ActualWidth - label.DesiredSize.Width);
            label.Margin = new Thickness(Math.Clamp(left, 0, maxLeft), 0, 0, 4);
        }
    }

    private void UpdateImageViewports()
    {
        if (_recording is null || _recording.DurationSeconds <= 0)
        {
            return;
        }

        var (visibleStart, visibleDuration) = GetVisibleTimeWindow();
        foreach (var image in _sonarImages)
        {
            if (image.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
            {
                continue;
            }

            var scale = _recording.DurationSeconds / visibleDuration;
            var imageWidth = parent.ActualWidth * scale;
            var left = -(visibleStart / _recording.DurationSeconds) * imageWidth;
            image.Width = imageWidth;
            image.Margin = new Thickness(left, 0, 0, 0);
        }
    }

    private (double Start, double Duration) GetVisibleTimeWindow()
    {
        if (_recording is null || _recording.DurationSeconds <= 0)
        {
            return (0, 1);
        }

        var duration = _zoomWindowSeconds <= 0
            ? _recording.DurationSeconds
            : Math.Min(_zoomWindowSeconds, _recording.DurationSeconds);
        var start = Math.Clamp(
            _playback.CurrentTimeSeconds - (duration / 2.0),
            0,
            Math.Max(0, _recording.DurationSeconds - duration));
        return (start, duration);
    }
}

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private double _opacity = 1.0;
    private BitmapSource? _image;

    public ChannelViewModel(ChannelTrack channel, BitmapSource? rawImage)
    {
        Channel = channel;
        Label = $"Channel {channel.ChannelId}";
        _image = rawImage ?? LoadRotatedPreviewImage(channel.WaterfallPath);
    }

    public ChannelTrack Channel { get; }

    public string Label { get; }

    public BitmapSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            OnPropertyChanged();
        }
    }

    public void SetImage(BitmapSource? image)
    {
        Image = image;
    }

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

    public static BitmapSource? LoadRotatedPreviewImage(string path)
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

public enum DepthUnit
{
    Feet,
    Meters,
    Fathoms
}

public enum SpeedUnit
{
    Mph,
    Knots
}

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit
}
