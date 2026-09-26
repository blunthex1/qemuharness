using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using QvmManager.Models;
using QvmManager.Services;

namespace QvmManager.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly VmLibrary _library;
    private readonly QemuEngine _engine;

    public DiagnosticsWindow(VmLibrary library, QemuEngine engine)
    {
        InitializeComponent();
        _library = library;
        _engine = engine;
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        SystemText.Text = BuildSystemReport();
        VmsText.Text = BuildVmsReport();
        LogText.Text = AppLog.ReadTail();
        LogText.ScrollToEnd();
        CopiedLabel.Visibility = Visibility.Collapsed;
    }

    private string BuildSystemReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("QVM Manager diagnostics");
        sb.AppendLine("Generated: " + DateTime.Now);
        sb.AppendLine();
        sb.AppendLine("OS:              " + Environment.OSVersion);
        sb.AppendLine(".NET runtime:    " + Environment.Version);
        sb.AppendLine("64-bit OS:       " + Environment.Is64BitOperatingSystem);
        sb.AppendLine("Processor count: " + Environment.ProcessorCount);
        sb.AppendLine();
        sb.AppendLine("Storage location (AppRoot): " + _library.AppRoot);
        sb.AppendLine("QEMU install dir:           " + _engine.QemuInstallDir);
        sb.AppendLine("QEMU installed:              " + _engine.IsQemuInstalled);
        if (_engine.IsQemuInstalled)
            sb.AppendLine("QEMU version:                " + _engine.QemuVersion());

        bool whpx;
        try { whpx = _engine.IsWhpxAvailable(); }
        catch (Exception ex) { whpx = false; sb.AppendLine("WHPX check failed: " + ex.Message); }
        sb.AppendLine("Windows Hypervisor Platform:  " + (whpx ? "Enabled" : "Disabled or unavailable"));
        if (!whpx)
            sb.AppendLine("  -> VMs will run under software emulation (TCG), which is much slower.");
        sb.AppendLine("  -> Known limit either way: WHPX gives no GPU/3D acceleration to guests.");

        sb.AppendLine();
        sb.AppendLine("Log file: " + AppLog.LogFile);

        return sb.ToString();
    }

    private string BuildVmsReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{_library.Vms.Count} VM(s) in {_library.VmsFolder}");
        sb.AppendLine();

        foreach (var vm in _library.Vms)
        {
            sb.AppendLine($"--- {vm.Name} ---");
            sb.AppendLine("  Id:              " + vm.Id);
            sb.AppendLine("  Guest OS type:   " + vm.OsType);
            sb.AppendLine("  Running now:     " + _engine.IsRunning(vm));
            sb.AppendLine("  Folder:          " + vm.FolderPath);
            sb.AppendLine("  Disk:            " + vm.DiskPath + "  (exists: " + File.Exists(vm.DiskPath) + ")");
            sb.AppendLine("  Disk size:       " + vm.DiskSizeGb + " GB (as created)");
            sb.AppendLine("  ISO:             " + (string.IsNullOrWhiteSpace(vm.IsoPath) ? "(none)" : vm.IsoPath +
                          "  (exists: " + File.Exists(vm.IsoPath) + ")"));
            sb.AppendLine("  Boot from ISO:   " + vm.BootFromIsoOnce);
            sb.AppendLine("  RAM / CPUs:      " + vm.RamMb + " MB / " + vm.Cpus);
            sb.AppendLine("  Machine type:    " + vm.MachineType);
            sb.AppendLine("  Disk interface:  " + vm.DiskInterface);
            sb.AppendLine("  Display (-vga):  " + vm.DisplayDevice + (string.IsNullOrWhiteSpace(vm.Resolution) ? "" : ", -g " + vm.Resolution));
            sb.AppendLine("  Network model:   " + vm.NetworkModel);
            sb.AppendLine("  Acceleration:    " + vm.EnableAcceleration);
            sb.AppendLine("  Audio:           " + vm.EnableAudio);
            sb.AppendLine("  USB tablet:      " + vm.AttachTabletDevice);
            if (!string.IsNullOrWhiteSpace(vm.ExtraArgs))
                sb.AppendLine("  Extra args:      " + vm.ExtraArgs);
            sb.AppendLine("  Created:         " + vm.CreatedAt);
            sb.AppendLine("  Last started:    " + (vm.LastStartedAt?.ToString() ?? "never"));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        var full = SystemText.Text + Environment.NewLine + Environment.NewLine +
                   "=== VMs ===" + Environment.NewLine + VmsText.Text + Environment.NewLine +
                   "=== Log (tail) ===" + Environment.NewLine + LogText.Text;

        Clipboard.SetText(full);
        CopiedLabel.Visibility = Visibility.Visible;
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppLog.LogFile))
        {
            MessageBox.Show(this, "No log file yet - nothing has been logged.", "Diagnostics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = AppLog.LogFile, UseShellExecute = true });
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(AppLog.LogFile)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "Clear the log file? This can't be undone.", "Diagnostics",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        AppLog.Clear();
        AppLog.Info("Log cleared from Diagnostics.");
        Refresh();
    }
}
