using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QvmManager.Models;
using QvmManager.Services;
using QvmManager.Views;

namespace QvmManager;

public partial class MainWindow : Window
{
    private readonly AppSettingsStore _settingsStore = new();
    private VmLibrary _library;
    private QemuEngine _engine;

    private VirtualMachine? _selected;
    private bool _suppressSettingsEvents;

    public MainWindow()
    {
        InitializeComponent();

        var appRoot = _settingsStore.Settings.AppRoot;
        if (string.IsNullOrWhiteSpace(appRoot))
        {
            appRoot = ChooseStorageFolder(
                "Choose where QVM Manager keeps its VMs and its own QEMU install. " +
                "Pick any drive/folder - you can move it later.",
                AppSettingsStore.DefaultAppRoot());

            // If the user cancels on first run, fall back to the default rather than crashing.
            if (string.IsNullOrWhiteSpace(appRoot))
                appRoot = AppSettingsStore.DefaultAppRoot();

            _settingsStore.Settings.AppRoot = appRoot;
            _settingsStore.Save();
        }

        _library = new VmLibrary(appRoot);
        _engine = new QemuEngine(appRoot);
        _engine.VmStopped += vm => Dispatcher.Invoke(() => RefreshList(vm.Id));

        RefreshList();
        Loaded += async (_, _) => await EnsureQemuInstalledAsync();
    }

    /// <summary>
    /// Shows a folder picker, seeded at <paramref name="suggested"/>, with an explanatory message first.
    /// Called from the constructor, before this window has a Win32 handle yet - passing "this" as the
    /// dialog owner at that point throws ("Operation is not valid due to the current state of the
    /// object"), so these run ownerless here.
    /// </summary>
    private string? ChooseStorageFolder(string explanation, string suggested)
    {
        MessageBox.Show(explanation, "QVM Manager", MessageBoxButton.OK, MessageBoxImage.Information);

        var dlg = new OpenFolderDialog
        {
            Title = "Choose a folder for QVM Manager's VMs and QEMU install",
            InitialDirectory = Directory.Exists(suggested)
                ? suggested
                : Path.GetPathRoot(suggested) ?? "",
        };

        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void StorageLocation_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose a new folder for QVM Manager's VMs and QEMU install",
            InitialDirectory = Directory.Exists(_library.AppRoot) ? _library.AppRoot : "",
        };
        if (dlg.ShowDialog(this) != true) return;

        var newRoot = dlg.FolderName;
        if (string.Equals(Path.GetFullPath(newRoot), Path.GetFullPath(_library.AppRoot), StringComparison.OrdinalIgnoreCase))
            return;

        var anyVms = _library.Vms.Count > 0;
        var moveExisting = false;
        if (anyVms || _engine.IsQemuInstalled)
        {
            var result = MessageBox.Show(this,
                $"Move your existing VMs and QEMU install from\n{_library.AppRoot}\nto\n{newRoot}?\n\n" +
                "Yes = move everything there (recommended).\nNo = just start using the new folder from here on (nothing already there is touched).",
                "Move existing files?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel) return;
            moveExisting = result == MessageBoxResult.Yes;
        }

        if (_library.Vms.Any(v => _engine.IsRunning(v)))
        {
            MessageBox.Show(this, "Stop all running VMs before changing the storage location.",
                "QVM Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (moveExisting)
            {
                StatusLine.Text = "Moving files - this can take a while for large disks...";
                var progress = new Progress<string>(msg => StatusLine.Text = msg);
                _library.MoveTo(newRoot, progress);
            }
            else
            {
                Directory.CreateDirectory(newRoot);
                _library = new VmLibrary(newRoot);
            }

            _engine.ChangeRoot(newRoot);
            _settingsStore.Settings.AppRoot = newRoot;
            _settingsStore.Save();

            RefreshList();
            LoadSelectedIntoPanel();
            StatusLine.Text = $"Storage location is now {newRoot}.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to change storage location", ex);
            MessageBox.Show(this, $"Couldn't switch storage location:\n{ex.Message}\n\nSee Diagnostics for the full error and log.",
                "Storage location", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.DiagnosticsWindow(_library, _engine) { Owner = this };
        dlg.Show(); // non-modal, so you can keep using the app (or reproduce a problem) while it's open
    }

    private async Task EnsureQemuInstalledAsync()
    {
        if (_engine.IsQemuInstalled) return;

        var result = MessageBox.Show(this,
            "QVM Manager needs QEMU (the free, open-source virtualization engine it runs on).\n\n" +
            "Download and install it now? (~70 MB, official build from qemu.weilnetz.de)",
            "Install QEMU", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            StatusLine.Text = "QEMU is not installed. VMs can't start until it is.";
            AppLog.Warn("User declined the QEMU install prompt.");
            return;
        }

        try
        {
            StatusLine.Text = "Downloading QEMU...";
            var progress = new Progress<string>(msg => StatusLine.Text = msg);
            await _engine.InstallQemuAsync(progress);
            StatusLine.Text = "QEMU installed.";
        }
        catch (Exception ex)
        {
            AppLog.Error("QEMU install failed", ex);
            MessageBox.Show(this, $"Couldn't install QEMU automatically:\n{ex.Message}\n\n" +
                "You can install it yourself from https://qemu.weilnetz.de/w64/ into:\n" + _engine.QemuInstallDir +
                "\n\nSee Diagnostics for the full error and log.",
                "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshList(string? keepSelectedId = null)
    {
        keepSelectedId ??= (_selected?.Id);

        foreach (var vm in _library.Vms)
            vm.StatusText = _engine.IsRunning(vm) ? "Running" : "Stopped";

        VmListBox.ItemsSource = null;
        VmListBox.ItemsSource = _library.Vms;

        if (keepSelectedId != null)
        {
            var match = _library.Vms.Find(v => v.Id == keepSelectedId);
            if (match != null) VmListBox.SelectedItem = match;
        }
    }

    private void NewVm_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewVmWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var vm = _library.CreateNew(dlg.VmName);
        vm.OsType = dlg.OsType;
        if (dlg.IsoPath != null) vm.IsoPath = dlg.IsoPath;

        // Sensible defaults by guest type.
        if (dlg.OsType == GuestOsType.WindowsGuest)
        {
            vm.RamMb = 8192;
            vm.Cpus = 4;
        }

        _library.Save();
        RefreshList(vm.Id);
    }

    private void VmListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = VmListBox.SelectedItem as VirtualMachine;
        LoadSelectedIntoPanel();
    }

    private void LoadSelectedIntoPanel()
    {
        _suppressSettingsEvents = true;
        try
        {
            if (_selected == null)
            {
                EmptyState.Visibility = Visibility.Visible;
                DetailsScroll.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            DetailsScroll.Visibility = Visibility.Visible;

            var running = _engine.IsRunning(_selected);

            VmTitle.Text = _selected.Name;
            VmSubtitle.Text = $"{_selected.OsType}  ·  {(running ? "Running" : "Stopped")}";
            RamBox.Text = _selected.RamMb.ToString();
            CpuBox.Text = _selected.Cpus.ToString();
            DiskSizeText.Text = _selected.DiskSizeGb + " GB (fixed at creation)";
            AccelCheck.IsChecked = _selected.EnableAcceleration;
            AudioCheck.IsChecked = _selected.EnableAudio;
            TabletCheck.IsChecked = _selected.AttachTabletDevice;
            IsoBox.Text = _selected.IsoPath ?? "";
            BootIsoCheck.IsChecked = _selected.BootFromIsoOnce;

            SelectByTag(MachineTypeBox, _selected.MachineType);
            SelectByTag(DiskInterfaceBox, _selected.DiskInterface);
            SelectByTag(DisplayBox, _selected.DisplayDevice);
            SelectByTag(ResolutionBox, _selected.Resolution);
            SelectByTag(NetworkModelBox, _selected.NetworkModel);
            ExtraArgsBox.Text = _selected.ExtraArgs;

            StartButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents || _selected == null) return;

        if (int.TryParse(RamBox.Text, out var ram) && ram > 0) _selected.RamMb = ram;
        if (int.TryParse(CpuBox.Text, out var cpus) && cpus > 0) _selected.Cpus = cpus;
        _selected.EnableAcceleration = AccelCheck.IsChecked == true;
        _selected.EnableAudio = AudioCheck.IsChecked == true;
        _selected.AttachTabletDevice = TabletCheck.IsChecked == true;
        _selected.BootFromIsoOnce = BootIsoCheck.IsChecked == true;

        _selected.MachineType = TagOf(MachineTypeBox) ?? _selected.MachineType;
        _selected.DiskInterface = TagOf(DiskInterfaceBox) ?? _selected.DiskInterface;
        _selected.DisplayDevice = TagOf(DisplayBox) ?? _selected.DisplayDevice;
        _selected.Resolution = TagOf(ResolutionBox) ?? _selected.Resolution;
        _selected.NetworkModel = TagOf(NetworkModelBox) ?? _selected.NetworkModel;
        _selected.ExtraArgs = ExtraArgsBox.Text;

        _library.Save();
    }

    /// <summary>Selects the ComboBoxItem whose Tag matches, falling back to the first item.</summary>
    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var obj in box.Items)
        {
            if (obj is ComboBoxItem item && (string?)item.Tag == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var dlg = new OpenFileDialog
        {
            Title = "Choose an installer ISO",
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            _selected.IsoPath = dlg.FileName;
            _selected.BootFromIsoOnce = true;
            IsoBox.Text = dlg.FileName;
            BootIsoCheck.IsChecked = true;
            _library.Save();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        if (!_engine.IsQemuInstalled)
        {
            await EnsureQemuInstalledAsync();
            if (!_engine.IsQemuInstalled) return;
        }

        try
        {
            _engine.Start(_selected);
            RefreshList(_selected.Id);
            LoadSelectedIntoPanel();
            StatusLine.Text = $"Started {_selected.Name}. Close the QEMU window to shut it off.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to start \"{_selected.Name}\"", ex);
            MessageBox.Show(this, $"Couldn't start the VM:\n{ex.Message}\n\nSee Diagnostics for the full error and log.",
                "Start failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _engine.Stop(_selected);
        StatusLine.Text = "Asked the VM to close. If it doesn't respond, close its QEMU window directly.";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = _selected.FolderPath,
            UseShellExecute = true
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        if (_engine.IsRunning(_selected))
        {
            MessageBox.Show(this, "Stop the VM before deleting it.", "QVM Manager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(this,
            $"Delete \"{_selected.Name}\"? This also deletes its virtual disk on disk. This can't be undone.",
            "Delete VM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            _library.Remove(_selected, deleteFiles: true);
            _selected = null;
            RefreshList();
            LoadSelectedIntoPanel();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to delete a VM", ex);
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
