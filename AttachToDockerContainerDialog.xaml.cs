using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using System.Web.Script.Serialization;
using EnvDTE;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AttachToDockerContainer
{
    public partial class AttachToDockerContainerDialog : DialogWindow
    {
        private const string VsDbgDefaultPath = "/vsdbg/vsdbg";

        private readonly IServiceProvider _serviceProvider;
        private readonly AttachToDockerContainerConfig _config;

        public AttachToDockerContainerDialog(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = ((AttachToDockerContainerPackage)serviceProvider).GetServiceAsync(typeof(DTE)) as DTE;

            _serviceProvider = serviceProvider;
            InitializeComponent();

            _config = GetConfig();

            AttachButton.IsEnabled = false;
            PidComboBox.IsEnabled = false;

            var containerNames = GetContainerNames();
            var processNames = GetProcessNames();
            var (previousContainer, previousVsDbgPath, previousProcessName) = GetSettings();

            ContainerComboBox.ItemsSource = containerNames;
            ContainerComboBox.Text = containerNames.Contains(previousContainer)
                ? previousContainer
                : containerNames.FirstOrDefault();

            VsDbgPathTextBox.Text = previousVsDbgPath ?? VsDbgDefaultPath;

            if (processNames.Any())
            {
                ProcessNameComboBox.IsEnabled = true;
                ProcessNameComboBox.ItemsSource = processNames;
                ProcessNameComboBox.Text = processNames.Contains(previousProcessName)
                    ? previousProcessName
                    : processNames.FirstOrDefault();
            }
            else
            {
                ProcessNameComboBox.IsEnabled = false;
                ProcessNameComboBox.ItemsSource = new[] { "" };
                ProcessNameComboBox.SelectedIndex = 0;
            }

            UpdateDotNetPIDs();

            ContainerComboBox.SelectionChanged += ContainerComboBox_SelectionChanged;
            ProcessNameComboBox.SelectionChanged += ProcessNameTextBox_SelectionChanged;
        }

        private void ProcessNameTextBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDotNetPIDs();
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var containerName = ContainerComboBox.Text;
            var vsDbgPath = VsDbgPathTextBox.Text;
            var processName = ProcessNameComboBox.Text;
            var pid = (int)PidComboBox.SelectedItem;

            SetSettings(containerName, vsDbgPath, processName);

            DebugAdapterHostLauncher.Instance.Launch(containerName, vsDbgPath, pid);
            Close();
        }

        private void ContainerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDotNetPIDs();
        }

        private void UpdateDotNetPIDs()
        {
            AttachButton.IsEnabled = false;
            PidComboBox.IsEnabled = false;

            var containerName = ContainerComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(containerName))
                return;

            var processName = ProcessNameComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(processName))
                return;

            var pidofResult = DockerCli.Execute($"exec -it {containerName} pidof {processName}");

            var dotnetPidsParsed = pidofResult
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(pidStr => (valid: int.TryParse(pidStr.Trim(), out int pid), pid))
                .ToArray();

            if (dotnetPidsParsed.Length == 0 || dotnetPidsParsed.Any(vp => !vp.valid))
            {
                PidComboBox.ItemsSource = new[] { "Cannot find dotnet process!" };
                PidComboBox.SelectedIndex = 0;
            }
            else
            {
                var dotnetPids = dotnetPidsParsed.Select(vp => vp.pid).ToArray();

                PidComboBox.ItemsSource = dotnetPids;
                PidComboBox.SelectedIndex = 0;

                if (dotnetPids.Length > 1)
                    PidComboBox.IsEnabled = true;

                if (dotnetPids.Length > 0)
                    AttachButton.IsEnabled = true;
            }
        }

        private string[] GetContainerNames()
        {
            var output = DockerCli.Execute("ps --format \"{{.Names}}\"");

            return output
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(n => n)
                .ToArray();
        }

        private string[] GetProcessNames()
        {
            return _config.DebuggableProcessNames;
        }

        private AttachToDockerContainerConfig GetConfig()
        {
            StatusMessage.Text = string.Empty;
            var config = new AttachToDockerContainerConfig();
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = (DTE)_serviceProvider.GetService(typeof(DTE));
                var solutionRoot = Path.GetDirectoryName(dte.Solution.FileName);

                var configFile = Path.Combine(solutionRoot, "container-debug-config.json");
                using (var sr = new StreamReader(configFile))
                {
                    string jsonString = sr.ReadToEnd();
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    config = ser.Deserialize<AttachToDockerContainerConfig>(jsonString);
                }
                if (!config.DebuggableProcessNames.Any())
                {
                    throw new Exception("Missing or empty DebuggableProcessNames.");
                }
                StatusMessage.Foreground = System.Windows.Media.Brushes.DarkGreen;
                StatusMessage.Text = "Configuration successfully loaded.";
                return config;
            }
            catch (Exception ex)
            {
                StatusMessage.Foreground = System.Windows.Media.Brushes.DarkRed;
                StatusMessage.Text = $"Problem loading <solution root>\\container-debug-config.json: {ex.Message}";
            }

            return config;
        }

        private (string container, string vsDbg, string processname) GetSettings()
        {
            const string collectionPath = nameof(AttachToDockerContainerDialog);

            ThreadHelper.ThrowIfNotOnUIThread();

            SettingsStore.CollectionExists(collectionPath, out int exists);
            if (exists != 1)
            {
                SettingsStore.CreateCollection(collectionPath);
            }

            SettingsStore.GetString(collectionPath, "container", out string container);
            SettingsStore.GetString(collectionPath, "vsdbg", out string vsdbg);
            SettingsStore.GetString(collectionPath, "processname", out string processname);

            return (container, vsdbg, processname);
        }

        private void SetSettings(string container, string vsdbg, string processname)
        {
            const string collectionPath = nameof(AttachToDockerContainerDialog);

            ThreadHelper.ThrowIfNotOnUIThread();

            SettingsStore.CollectionExists(collectionPath, out int exists);
            if (exists != 1)
            {
                SettingsStore.CreateCollection(collectionPath);
            }

            SettingsStore.SetString(collectionPath, "container", container);
            SettingsStore.SetString(collectionPath, "vsdbg", vsdbg);
            SettingsStore.SetString(collectionPath, "processname", processname);
        }

        private IVsWritableSettingsStore _settingsStore = null;
        private IVsWritableSettingsStore SettingsStore
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_settingsStore == null)
                {
                    var settingsManager = (IVsSettingsManager)_serviceProvider.GetService(typeof(SVsSettingsManager));

                    // Write the user settings to _settingsStore.
                    settingsManager.GetWritableSettingsStore(
                        (uint)__VsSettingsScope.SettingsScope_UserSettings,
                        out _settingsStore);
                }
                return _settingsStore;
            }
        }

        public class AttachToDockerContainerConfig
        {
            public AttachToDockerContainerConfig()
            {
                DebuggableProcessNames = Array.Empty<string>();
            }

            public string[] DebuggableProcessNames { get; set; }
        }
    }
}
