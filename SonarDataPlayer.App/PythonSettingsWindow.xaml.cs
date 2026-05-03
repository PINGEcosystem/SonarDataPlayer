using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace SonarDataPlayer.App;

public partial class PythonSettingsWindow : Window
{
    private const string PythonEnvVar = "SONAR_DATA_PLAYER_PYTHON";

    public PythonSettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        Settings = settings;
        EnvPathText.Text = Environment.GetEnvironmentVariable(PythonEnvVar) ?? "";
        PythonPathText.Text = settings.PythonPath ?? "";
        UseEnvRadio.IsChecked = settings.UseEnvironmentPython;
        UsePathRadio.IsChecked = !settings.UseEnvironmentPython;
        TestOutputText.Text = "Click Test to run the selected Python interpreter.";
    }

    public AppSettings Settings { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Python executable",
            Filter = "Python executable (python.exe)|python.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };

        if (!string.IsNullOrWhiteSpace(PythonPathText.Text) && File.Exists(PythonPathText.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(PythonPathText.Text);
            dialog.FileName = Path.GetFileName(PythonPathText.Text);
        }

        if (dialog.ShowDialog(this) == true)
        {
            PythonPathText.Text = dialog.FileName;
            UsePathRadio.IsChecked = true;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var pythonPath = SelectedPythonPath();
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            TestOutputText.Text = UseEnvRadio.IsChecked == true
                ? $"{PythonEnvVar} is not set."
                : "No saved Python path has been selected.";
            return;
        }

        TestOutputText.Text = $"Testing {pythonPath}...";
        TestOutputText.Text = await TestPythonAsync(pythonPath);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings = new AppSettings(
            PythonPath: string.IsNullOrWhiteSpace(PythonPathText.Text) ? null : PythonPathText.Text.Trim(),
            UseEnvironmentPython: UseEnvRadio.IsChecked == true);
        Settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private string? SelectedPythonPath()
    {
        if (UseEnvRadio.IsChecked == true)
        {
            return Environment.GetEnvironmentVariable(PythonEnvVar);
        }

        return PythonPathText.Text.Trim();
    }

    private static async Task<string> TestPythonAsync(string pythonPath)
    {
        if (Path.IsPathFullyQualified(pythonPath) && !File.Exists(pythonPath))
        {
            return $"Python executable does not exist:\n{pythonPath}";
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
        process.StartInfo.ArgumentList.Add(
            "import sys; print(sys.version.replace('\\n', ' '));\n" +
            "try:\n" +
            " import numpy, pandas\n" +
            " print('numpy:', numpy.__version__)\n" +
            " print('pandas:', pandas.__version__)\n" +
            "except Exception as exc:\n" +
            " print('dependency_error:', exc)\n");

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var status = process.ExitCode == 0 ? "OK" : $"Exit code {process.ExitCode}";
            return string.Join(
                "\n\n",
                new[]
                {
                    $"Python: {pythonPath}",
                    $"Status: {status}",
                    string.IsNullOrWhiteSpace(stdout) ? null : stdout.Trim(),
                    string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim()
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        catch (Exception ex)
        {
            return $"Failed to run Python:\n{ex.Message}";
        }
    }
}
