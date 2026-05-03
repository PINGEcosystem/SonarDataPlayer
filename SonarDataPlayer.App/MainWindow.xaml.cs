using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SonarDataPlayer.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Line = System.Windows.Shapes.Line;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using WpfImage = System.Windows.Controls.Image;

namespace SonarDataPlayer.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelViewModel> _channels = new();
    private readonly PlaybackState _playback = new();
    private readonly DispatcherTimer _timer;
    private readonly List<WpfImage> _sonarImages = new();
    private readonly List<Canvas> _depthGrids = new();
    private readonly List<Rectangle> _timeCursors = new();
    private readonly List<TextBlock> _cursorTimeLabels = new();
    private TextBlock? _viewerDepthReadout;
    private TextBlock? _viewerTempReadout;
    private SonarRecording? _recording;
    private IReadOnlyDictionary<int, BitmapSource> _rawChannelImages = new Dictionary<int, BitmapSource>();
    private double? _manualMaxDepthMeters;
    private bool _isDepthAutoRange = true;
    private double _autoMaxDepthMeters;
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    private bool _isUpdatingSeek;
    private DepthUnit _depthUnit = DepthUnit.Meters;
    private SpeedUnit _speedUnit = SpeedUnit.Mph;
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;
    private double _zoomWindowSeconds;
    private int _utcOffsetHours = -4;
    private AppSettings _settings = AppSettings.Load();

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

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.OpenProjectAfterProcessing && File.Exists(dialog.ManifestPath))
        {
            LoadRecording(dialog.ManifestPath);
        }
    }

    private void PythonSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PythonSettingsWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.Settings;
            ProjectStatusText.Text = "Python settings saved";
            ProjectStatusText.Foreground = new SolidColorBrush(Color.FromRgb(88, 214, 141));
        }
    }

    internal static string? FindPythonExecutable(AppSettings settings)
    {
        var configured = Environment.GetEnvironmentVariable("SONAR_DATA_PLAYER_PYTHON");
        var candidates = new List<string>();

        if (settings.UseEnvironmentPython)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }

            if (!string.IsNullOrWhiteSpace(settings.PythonPath))
            {
                candidates.Add(settings.PythonPath);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.PythonPath))
            {
                candidates.Add(settings.PythonPath);
            }

            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }
        }

        candidates.AddRange(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".venv", "Scripts", "python.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "PINGverter", ".venv", "Scripts", "python.exe")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache",
                "codex-runtimes",
                "codex-primary-runtime",
                "dependencies",
                "python",
                "python.exe"),
            "python",
            "py"
        });

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(PythonHasParserDependencies);
    }

    internal static bool PythonHasParserDependencies(string pythonPath)
    {
        if (Path.IsPathFullyQualified(pythonPath) && !File.Exists(pythonPath))
        {
            return false;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("import numpy, pandas, PIL");

        try
        {
            process.Start();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void LoadRecording(string manifestPath)
    {
        _recording = ProcessedProjectLoader.Load(manifestPath);
        _manualMaxDepthMeters = null;
        _isDepthAutoRange = true;
        _autoMaxDepthMeters = GetAutoMaxRangeMeters();
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
        UpdateDepthAutoButtonState();
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

        if (_recording is not null && _isDepthAutoRange)
        {
            _autoMaxDepthMeters = GetAutoMaxRangeMeters();
            RebuildDepthScaledView();
            return;
        }

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
        _isDepthAutoRange = false;
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
        _isDepthAutoRange = false;
        _manualMaxDepthMeters = Math.Min(GetFileMaxRangeMeters(), Math.Max(auto, current * 1.25));
        RebuildDepthScaledView();
    }

    private void DepthZoomAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        _isDepthAutoRange = true;
        _manualMaxDepthMeters = null;
        _autoMaxDepthMeters = GetAutoMaxRangeMeters();
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

        if (_isDepthAutoRange && UpdateAutoRangeFromDepth(ping.DepthMeters))
        {
            RebuildDepthScaledView();
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
        _depthGrids.Clear();
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
        UpdateDepthAutoButtonState();
    }

    private void RenderRawChannelImages()
    {
        var displayMaxRange = GetDisplayMaxRangeMeters();
        _rawChannelImages = _recording is null
            ? new Dictionary<int, BitmapSource>()
            : BinaryWaterfallRenderer.Render(_recording, displayMaxRange > 0 ? displayMaxRange : null);
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

    private WpfImage CreateSonarImage(ChannelViewModel channel)
    {
        var image = new WpfImage
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
        _depthGrids.Add(canvas);
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
        var (visibleStart, visibleDuration) = GetVisibleTimeWindow();
        var visibleEnd = visibleStart + visibleDuration;
        for (var i = 0; i < _recording.Frames.Count; i++)
        {
            var frame = _recording.Frames[i];
            if (frame.TimeSeconds < visibleStart || frame.TimeSeconds > visibleEnd)
            {
                continue;
            }

            var block = _recording.Frames[i].Channels.FirstOrDefault(c => c.ChannelId == channelId.Value);
            if (block?.BottomDepthMeters is not { } bottom || bottom <= 0)
            {
                continue;
            }

            var x = ((frame.TimeSeconds - visibleStart) / visibleDuration) * canvas.ActualWidth;
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
        return _isDepthAutoRange
            ? _autoMaxDepthMeters
            : _manualMaxDepthMeters ?? _autoMaxDepthMeters;
    }

    private double GetAutoMaxRangeMeters()
    {
        var ping = _recording?.FindNearestTelemetry(_playback.CurrentTimeSeconds);
        return GetAutoMaxRangeMeters(ping?.DepthMeters);
    }

    private double GetAutoMaxRangeMeters(double? currentDepthMeters)
    {
        if (currentDepthMeters.HasValue && currentDepthMeters.Value > 0)
        {
            return CalculateAutoDepthRangeMeters(currentDepthMeters.Value);
        }

        return GetFileMaxRangeMeters();
    }

    private double GetFileMaxRangeMeters()
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

    private bool UpdateAutoRangeFromDepth(double? currentDepthMeters)
    {
        if (!_isDepthAutoRange)
        {
            return false;
        }

        if (!currentDepthMeters.HasValue || currentDepthMeters.Value <= 0)
        {
            return false;
        }

        var target = GetAutoDepthTargetMeters(currentDepthMeters.Value);
        var next = CalculateAutoDepthRangeMeters(currentDepthMeters.Value);
        var gridLine = GetDepthGridIntervalMeters();

        if (_autoMaxDepthMeters > 0 && next < _autoMaxDepthMeters)
        {
            var shrinkThreshold = _autoMaxDepthMeters - (3 * gridLine);
            if (target >= shrinkThreshold)
            {
                return false;
            }
        }

        if (next <= 0 || Math.Abs(next - _autoMaxDepthMeters) < 0.001)
        {
            return false;
        }

        _autoMaxDepthMeters = next;
        return true;
    }

    private double CalculateAutoDepthRangeMeters(double depthMeters)
    {
        var targetMeters = GetAutoDepthTargetMeters(depthMeters);
        var quantumMeters = GetDepthGridIntervalMeters() * 2.0;
        var minMeters = 10.0 / 3.280839895;

        if (targetMeters <= minMeters)
        {
            return minMeters;
        }

        return Math.Ceiling(targetMeters / quantumMeters) * quantumMeters;
    }

    private double GetAutoDepthTargetMeters(double depthMeters)
    {
        var depthFeet = depthMeters * 3.280839895;
        if (depthFeet < 10)
        {
            return 10.0 / 3.280839895;
        }

        var factor = depthFeet > 1000 ? 1.10 : 1.20;
        return depthMeters * factor;
    }

    private double GetDepthGridIntervalMeters()
    {
        return _depthUnit switch
        {
            DepthUnit.Feet => 10.0 / 3.280839895,
            DepthUnit.Fathoms => 1.8288,
            _ => 3.0
        };
    }

    private void UpdateDepthAutoButtonState()
    {
        if (DepthZoomAutoButton is null)
        {
            return;
        }

        if (_isDepthAutoRange)
        {
            DepthZoomAutoButton.Background = new SolidColorBrush(Color.FromRgb(46, 138, 87));
            DepthZoomAutoButton.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 220, 139));
            DepthZoomAutoButton.Foreground = Brushes.White;
        }
        else
        {
            DepthZoomAutoButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            DepthZoomAutoButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            DepthZoomAutoButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
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

        foreach (var grid in _depthGrids)
        {
            DrawDepthGrid(grid);
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
        Label = $"{channel.Label} ({channel.ChannelId})";
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

public sealed record AppSettings(
    string? PythonPath = null,
    bool UseEnvironmentPython = true,
    string? PingverterRoot = null,
    string? ProjectsRoot = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SonarDataPlayer",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings(PythonPath: null);
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings(PythonPath: null);
        }
        catch
        {
            return new AppSettings(PythonPath: null);
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
