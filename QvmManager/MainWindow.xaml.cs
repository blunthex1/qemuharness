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

        // Default is 1600x900; shrink to fit on smaller screens so it never opens off-screen.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width * 0.95);
        Height = Math.Min(Height, work.Height * 0.95);

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
        Loaded += async (_, _) =>
        {
            await EnsureQemuInstalledAsync();
            await CheckQemuUpdateAsync(manual: false);
        };
    }

    /// <summary>
    /// Clicking X really exits: offers to stop any VMs still running, then shuts the whole
    /// process down so no invisible QvmManager.exe is left behind holding files locked.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var running = _engine?.RunningCount ?? 0;
        if (running > 0)
        {
            var answer = MessageBox.Show(this,
                $"{running} VM(s) are still running.\n\n" +
                "Yes = stop them and exit\nNo = exit and leave them running\nCancel = don't exit",
                "QVM Manager", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes)
            {
                AppLog.Info($"Exiting: force-stopping {running} running VM(s).");
                _engine!.KillAll();
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        AppLog.Info("=== QVM Manager closed ===");
        Application.Current.Shutdown();
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

    private void CheckQemuUpdate_Click(object sender, RoutedEventArgs e) => _ = CheckQemuUpdateAsync(manual: true);

    /// <summary>
    /// Compares the installed QEMU against the newest build on the download site and offers to update.
    /// On startup (manual=false) it stays quiet when up to date or offline, and doesn't re-ask about a
    /// build the user already said "not now" to.
    /// </summary>
    private async Task CheckQemuUpdateAsync(bool manual)
    {
        if (!_engine.IsQemuInstalled) return;

        if (manual) StatusLine.Text = "Checking for a newer QEMU...";
        var latest = await _engine.CheckForUpdateAsync();
        var installed = _engine.InstalledBuildDate();

        if (latest == null)
        {
            if (manual)
            {
                StatusLine.Text = "QEMU is up to date.";
                MessageBox.Show(this, $"QEMU is up to date (installed build: {installed:yyyy-MM-dd}).

" +
                    "If you expected an update, check your internet connection - see Diagnostics for details.",
                    "QEMU update", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        if (!manual && _settingsStore.Settings.SkippedQemuBuild == latest.SetupName) return;

        if (_engine.RunningCount > 0)
        {
            StatusLine.Text = $"A QEMU update is available ({latest.BuildDate:yyyy-MM-dd}). Stop your VMs, then use \"Check for QEMU update\".";
            if (manual)
                MessageBox.Show(this, "A QEMU update is available, but VMs are running. Stop them first, then check again.",
                    "QEMU update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"A newer QEMU is available.

Installed: {installed:yyyy-MM-dd}
Latest:    {latest.BuildDate:yyyy-MM-dd}

" +
            "Update now? It installs over the current QEMU (a Windows admin prompt will appear). " +
            "Your VMs and disks aren't touched.

No = skip this version.",
            "QEMU update", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            _settingsStore.Settings.SkippedQemuBuild = latest.SetupName;
            _settingsStore.Save();
            AppLog.Info($"User skipped QEMU update {latest}.");
            StatusLine.Text = "QEMU update skipped. Use \"Check for QEMU update\" to get it later.";
            return;
        }

        try
        {
            var progress = new Progress<string>(msg => StatusLine.Text = msg);
            await _engine.InstallQemuAsync(progress, latest);
            _settingsStore.Settings.SkippedQemuBuild = null;
            _settingsStore.Save();
            StatusLine.Text = "QEMU updated: " + _engine.QemuVersion();
        }
        catch (Exception ex)
        {
            AppLog.Error("QEMU update failed", ex);
            MessageBox.Show(this, $"Couldn't update QEMU:\n{ex.Message}\n\nYour current QEMU is still installed. See Diagnostics for details.",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            CpuModelBox.Text = _selected.CpuModel ?? "";
            CpuFlagsBox.Text = _selected.CpuFlags ?? "";
            CpuPresetBox.SelectedIndex = 0;
            UpdateCpuPreview();
            DiskSizeText.Text = _selected.ImportedDisk
                ? $"Imported: {_selected.DiskPath} ({_selected.DiskFormat})"
                : $"{_selected.DiskSizeGb} GB {_selected.DiskFormat} (created by QVM Manager)";
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
        _selected.CpuModel = CpuModelBox.Text.Trim();
        _selected.CpuFlags = CpuFlagsBox.Text.Trim();
        UpdateCpuPreview();
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

    /// <summary>Fills the CPU model + flags boxes from the chosen preset ("model|flags" in the Tag).</summary>
    private void CpuPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents || _selected == null) return;
        var tag = TagOf(CpuPresetBox);
        if (string.IsNullOrEmpty(tag)) return; // the "(pick a preset...)" placeholder

        var parts = tag.Split('|');
        CpuModelBox.Text = parts[0];
        CpuFlagsBox.Text = parts.Length > 1 ? parts[1] : "";
        Settings_Changed(sender, e);
    }

    /// <summary>Shows the exact -cpu value that will be passed to QEMU.</summary>
    private void UpdateCpuPreview()
    {
        if (_selected == null) { CpuPreviewText.Text = ""; return; }
        var probe = new VirtualMachine { Name = _selected.Name, CpuModel = CpuModelBox.Text, CpuFlags = CpuFlagsBox.Text };
        var withWhpx = QemuEngine.BuildCpuArg(probe, whpx: true);
        var withoutWhpx = QemuEngine.BuildCpuArg(probe, whpx: false);
        CpuPreviewText.Text = withWhpx == withoutWhpx
            ? $"Will pass:  -cpu {withWhpx}"
            : $"Will pass:  -cpu {withWhpx}  (with WHPX)   /   -cpu {withoutWhpx}  (without)";
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

    private void ImportDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        if (_engine.IsRunning(_selected))
        {
            MessageBox.Show(this, "Stop the VM before changing its disk.", "QVM Manager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hadExistingDisk = File.Exists(_selected.DiskPath);
        var result = MessageBox.Show(this,
            "Point this VM at a disk image you already have (e.g. from another QEMU setup, or " +
            "exported from VirtualBox/VMware)?\n\n" +
            (hadExistingDisk
                ? "This VM already has a disk - it will be replaced (the old one isn't deleted from disk, just unlinked)."
                : "This VM doesn't have a disk yet, so nothing is lost either way."),
            "Import existing disk", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        var dlg = new OpenFileDialog
        {
            Title = "Choose an existing disk image",
            Filter = "Disk images (*.qcow2;*.img;*.raw;*.vdi;*.vmdk;*.vhd;*.vhdx)|*.qcow2;*.img;*.raw;*.vdi;*.vmdk;*.vhd;*.vhdx|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _selected.DiskPath = dlg.FileName;
        _selected.DiskFormat = GuessDiskFormat(dlg.FileName);
        _selected.ImportedDisk = true;
        _library.Save();

        AppLog.Info($"\"{_selected.Name}\" now points at imported disk {dlg.FileName} (format: {_selected.DiskFormat})");
        LoadSelectedIntoPanel();
        StatusLine.Text = $"Now using the imported disk. Format guessed as \"{_selected.DiskFormat}\" - " +
                           "change it back in Diagnostics/support if that's wrong for this file.";
    }

    /// <summary>Guesses a qemu-img -f format from the file's extension. VirtualBox uses .vdi, VMware uses .vmdk, Hyper-V uses .vhd/.vhdx.</summary>
    private static string GuessDiskFormat(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".qcow2" => "qcow2",
            ".vdi" => "vdi",
            ".vmdk" => "vmdk",
            ".vhd" => "vpc",
            ".vhdx" => "vhdx",
            ".img" or ".raw" => "raw",
            _ => "raw",
        };

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
