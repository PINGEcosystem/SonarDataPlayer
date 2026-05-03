using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Color = System.Windows.Media.Color;

namespace SonarDataPlayer.App;

public partial class NewProjectWindow : Window
{
    private readonly AppSettings _settings;
    private bool _processed;

    public NewProjectWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        var pingverterRoot = ResolvePingverterRoot(settings);
        PingverterRootText.Text = pingverterRoot ?? "Not configured. Set this in Python settings.";
        OutputFolderText.Text = DefaultProjectFolder();
        AppendOutput("Select a recording and output folder, then click Process Recording.");
    }

    public string ManifestPath { get; private set; } = "";

    public bool OpenProjectAfterProcessing => OpenProjectCheck.IsChecked == true;

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select sonar recording",
            Filter = "Sonar recordings (*.rsd;*.RSD;*.sl2;*.SL2;*.sl3;*.SL3)|*.rsd;*.RSD;*.sl2;*.SL2;*.sl3;*.SL3|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputFileText.Text = dialog.FileName;
            OutputFolderText.Text = Path.Combine(DefaultProjectFolder(), Path.GetFileNameWithoutExtension(dialog.FileName));
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose or create the SonarDataPlayer project folder",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrWhiteSpace(OutputFolderText.Text))
        {
            dialog.SelectedPath = OutputFolderText.Text;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputFolderText.Text = dialog.SelectedPath;
        }
    }

    private async void Process_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var pythonPath, out var pingverterRoot, out var inputFile, out var outputFolder))
        {
            return;
        }

        ProcessButton.IsEnabled = false;
        ProcessButton.Content = "Processing...";
        OutputText.Clear();
        AppendOutput($"Python: {pythonPath}");
        AppendOutput($"PINGverter: {pingverterRoot}");
        AppendOutput($"Input: {inputFile}");
        AppendOutput($"Output: {outputFolder}");
        AppendOutput("");

        Directory.CreateDirectory(outputFolder);
        var exitCode = await RunPingverterAsync(pythonPath, pingverterRoot, inputFile, outputFolder);
        ManifestPath = Path.Combine(outputFolder, "manifest.json");

        if (exitCode == 0 && File.Exists(ManifestPath))
        {
            _processed = true;
            ProcessButton.Content = "Complete";
            ProcessButton.Background = new SolidColorBrush(Color.FromRgb(46, 138, 87));
            ProcessButton.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 220, 139));
            AppendOutput("");
            AppendOutput($"Done. Wrote {ManifestPath}");
            if (OpenProjectAfterProcessing)
            {
                DialogResult = true;
            }
        }
        else
        {
            ProcessButton.IsEnabled = true;
            ProcessButton.Content = "Process Recording";
            AppendOutput("");
            AppendOutput(exitCode == 0
                ? "Conversion finished, but manifest.json was not created."
                : $"Conversion failed with exit code {exitCode}.");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _processed;
    }

    private bool ValidateInputs(out string pythonPath, out string pingverterRoot, out string inputFile, out string outputFolder)
    {
        pythonPath = MainWindow.FindPythonExecutable(_settings) ?? "";
        pingverterRoot = ResolvePingverterRoot(_settings) ?? "";
        inputFile = InputFileText.Text.Trim();
        outputFolder = OutputFolderText.Text.Trim();

        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            AppendOutput("No usable Python was found. Configure Python settings first.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(pingverterRoot) || !Directory.Exists(Path.Combine(pingverterRoot, "pingverter")))
        {
            AppendOutput("PINGverter root is not configured or does not contain a pingverter folder.");
            return false;
        }

        if (!File.Exists(inputFile))
        {
            AppendOutput("Input file does not exist.");
            return false;
        }

        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        if (ext is not ".rsd" and not ".sl2" and not ".sl3")
        {
            AppendOutput("Input must be .rsd, .sl2, or .sl3.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            AppendOutput("Choose a project folder.");
            return false;
        }

        return true;
    }

    private async Task<int> RunPingverterAsync(string pythonPath, string pingverterRoot, string inputFile, string outputFolder)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                WorkingDirectory = pingverterRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(PingverterRunnerCode);
        process.StartInfo.ArgumentList.Add(pingverterRoot);
        process.StartInfo.ArgumentList.Add(inputFile);
        process.StartInfo.ArgumentList.Add(outputFolder);

        var done = new TaskCompletionSource<int>();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Dispatcher.Invoke(() => AppendOutput(args.Data));
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Dispatcher.Invoke(() => AppendOutput(args.Data));
            }
        };
        process.Exited += (_, _) => done.TrySetResult(process.ExitCode);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return await done.Task;
        }
        catch (Exception ex)
        {
            AppendOutput($"Failed to start Python: {ex.Message}");
            return -1;
        }
    }

    private void AppendOutput(string text)
    {
        OutputText.AppendText(text + Environment.NewLine);
        OutputText.ScrollToEnd();
    }

    private static string DefaultProjectFolder()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ProcessedRecordings"));
    }

    private static string? ResolvePingverterRoot(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PingverterRoot) &&
            Directory.Exists(Path.Combine(settings.PingverterRoot, "pingverter")))
        {
            return settings.PingverterRoot;
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "PINGverter")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "PINGverter"))
        };

        return candidates.FirstOrDefault(path => Directory.Exists(Path.Combine(path, "pingverter")));
    }

    private const string PingverterRunnerCode = """
import importlib.util
import os
import sys
import types

root, source, output = sys.argv[1], sys.argv[2], sys.argv[3]
package_dir = os.path.join(root, "pingverter")
if not os.path.isdir(package_dir):
    raise FileNotFoundError(f"PINGverter package folder not found: {package_dir}")

package = types.ModuleType("pingverter")
package.__path__ = [package_dir]
sys.modules["pingverter"] = package

def load_module(name):
    path = os.path.join(package_dir, name + ".py")
    spec = importlib.util.spec_from_file_location("pingverter." + name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Could not load {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules["pingverter." + name] = module
    spec.loader.exec_module(module)
    return module

ext = os.path.splitext(source)[1].lower()
print(f"Loading PINGverter from {root}", flush=True)
print(f"Detected input format: {ext}", flush=True)

if ext == ".rsd":
    module = load_module("garmin_class")
    sonar = module.gar(source, nchunk=500, exportUnknown=True)
elif ext in (".sl2", ".sl3"):
    module = load_module("lowrance_class")
    sonar = module.low(source, nchunk=500, exportUnknown=True)
    sonar.tempC = 1.0
else:
    raise ValueError(f"Unsupported recording extension: {ext}")

if not hasattr(sonar, "write_sonar_data_player_project"):
    raise AttributeError("Configured PINGverter checkout does not expose write_sonar_data_player_project for this format.")

print("Parsing recording and writing project...", flush=True)
manifest = sonar.write_sonar_data_player_project(output, include_pngs=True)
print(f"Manifest: {manifest}", flush=True)
print("Conversion complete.", flush=True)
""";
}
