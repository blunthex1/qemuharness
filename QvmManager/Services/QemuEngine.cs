using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QvmManager.Models;

namespace QvmManager.Services;

/// <summary>
/// Locates/installs QEMU, builds the right command line for a VM, and launches/tracks it.
///
/// Notes carried over from hand-debugging a raw PowerShell QEMU launcher:
///   - Windows Hypervisor Platform (WHPX) can't reliably mask individual CPU features off a
///     named model (e.g. "Haswell,-hle,-rtm,..."); it wants "-cpu host" (or close to it) or
///     it can hang/fail to boot. We use "-cpu host" whenever WHPX is active, and only use a
///     named/masked model on the unaccelerated TCG fallback.
///   - Never build a single joined command-line string and hand it to a shell/Start-Process
///     the "loose" way; that silently breaks any path with a space in it. We use
///     ProcessStartInfo.ArgumentList (one array entry per argument), which quotes each entry
///     correctly regardless of spaces - the .NET equivalent of PowerShell's "& exe @args" fix.
/// </summary>
public class QemuEngine
{
    public string QemuInstallDir { get; private set; }
    public string QemuExe => Path.Combine(QemuInstallDir, "qemu-system-x86_64.exe");
    public string QemuImgExe => Path.Combine(QemuInstallDir, "qemu-img.exe");

    private readonly Dictionary<string, Process> _running = new();

    /// <param name="appRoot">The top-level folder (see AppSettingsStore) - qemu is installed at "&lt;appRoot&gt;\qemu".</param>
    public QemuEngine(string appRoot)
    {
        QemuInstallDir = Path.GetFullPath(Path.Combine(appRoot, "qemu"));
    }

    /// <summary>Re-points at a new app root after VmLibrary.MoveTo has relocated the qemu folder.</summary>
    public void ChangeRoot(string newAppRoot)
    {
        QemuInstallDir = Path.GetFullPath(Path.Combine(newAppRoot, "qemu"));
    }

    public bool IsQemuInstalled => File.Exists(QemuExe) && File.Exists(QemuImgExe);

    /// <summary>Downloads and silently installs the official Stefan Weil QEMU Windows build.</summary>
    public async Task InstallQemuAsync(IProgress<string>? progress = null)
    {
        AppLog.Info("Starting QEMU install into " + QemuInstallDir);
        Directory.CreateDirectory(QemuInstallDir);

        progress?.Report("Finding the latest QEMU build...");
        const string baseUrl = "https://qemu.weilnetz.de/w64/";
        using var http = new HttpClient();
        var html = await http.GetStringAsync(baseUrl);

        var matches = Regex.Matches(html, @"qemu-w64-setup-\d+\.exe")
            .Select(m => m.Value)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        if (matches.Count == 0)
            throw new Exception($"Couldn't find a QEMU installer at {baseUrl}");

        var setupName = matches.Last();
        var setupPath = Path.Combine(QemuInstallDir, setupName);

        progress?.Report($"Downloading {setupName}...");
        var bytes = await http.GetByteArrayAsync(baseUrl + setupName);
        await File.WriteAllBytesAsync(setupPath, bytes);

        progress?.Report("Installing QEMU (a Windows admin prompt will pop up - click Yes)...");
        var psi = new ProcessStartInfo
        {
            FileName = setupPath,
            // The QEMU installer's own manifest requires elevation, so it has to be launched
            // through the shell with the "runas" verb to trigger the UAC prompt. UseShellExecute=false
            // (a plain child process) fails with "The requested operation requires elevation."
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"/S /D={QemuInstallDir}",
        };

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: user clicked "No" on the UAC prompt.
            throw new Exception("The admin prompt was declined, so QEMU wasn't installed. Run this again and click \"Yes\" on the Windows prompt.");
        }

        using (proc)
        {
            await proc.WaitForExitAsync();
        }

        try { File.Delete(setupPath); } catch { /* best effort */ }

        if (!IsQemuInstalled)
        {
            AppLog.Error($"QEMU install finished but {QemuExe} was not found.");
            throw new Exception("QEMU installer finished but qemu-system-x86_64.exe was not found. Try installing manually.");
        }

        AppLog.Info("QEMU install finished successfully: " + QemuVersion());
    }

    /// <summary>Runs "qemu-system-x86_64 --version" for the diagnostics panel. Returns a short error string instead of throwing.</summary>
    public string QemuVersion()
    {
        if (!IsQemuInstalled) return "(not installed)";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = QemuExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadLine() ?? "";
            proc.WaitForExit(3000);
            return output.Trim();
        }
        catch (Exception ex)
        {
            return $"(couldn't read version: {ex.Message})";
        }
    }

    public void CreateDisk(string diskPath, string sizeGb, string format = "qcow2")
    {
        if (File.Exists(diskPath)) return;

        var psi = new ProcessStartInfo
        {
            FileName = QemuImgExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("create");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add(diskPath);
        psi.ArgumentList.Add(sizeGb + "G");

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            AppLog.Error($"qemu-img create failed ({proc.ExitCode}) for {diskPath}: {err}");
            throw new Exception($"qemu-img failed ({proc.ExitCode}): {err}");
        }
    }

    [System.Runtime.InteropServices.DllImport("WinHvPlatform.dll")]
    private static extern int WHvGetCapability(int capabilityCode, out int capabilityBuffer,
        uint capabilityBufferSizeInBytes, out uint writtenSizeInBytes);

    /// <summary>
    /// Whether Windows Hypervisor Platform is usable right now. Asks the hypervisor API directly
    /// (WHvGetCapability / HypervisorPresent), which works without admin rights. The old check,
    /// Get-WindowsOptionalFeature, needs an elevated shell - run normally it errored, which was
    /// read as "not available" and silently dropped every VM to slow TCG even with WHPX on.
    /// </summary>
    public bool IsWhpxAvailable()
    {
        try
        {
            // WHvCapabilityCodeHypervisorPresent = 0. WinHvPlatform.dll only exists when the
            // feature is installed, so a missing DLL also means "not available".
            var hr = WHvGetCapability(0, out var present, sizeof(int), out _);
            var ok = hr == 0 && present != 0;
            AppLog.Info($"WHPX check: WHvGetCapability hr=0x{hr:X8}, hypervisorPresent={present} -> {(ok ? "available" : "not available")}");
            return ok;
        }
        catch (DllNotFoundException)
        {
            AppLog.Info("WHPX check: WinHvPlatform.dll not found -> Windows Hypervisor Platform isn't installed.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("WHPX check via WHvGetCapability failed, falling back to the feature query: " + ex.Message);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("(Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform).State");

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return output.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // can't tell -> assume off, fall back to TCG (slower but safe)
        }
    }

    public bool IsRunning(VirtualMachine vm) =>
        _running.TryGetValue(vm.Id, out var p) && !p.HasExited;

    public event Action<VirtualMachine>? VmStopped;

    public void Start(VirtualMachine vm)
    {
        if (IsRunning(vm)) return;
        if (!IsQemuInstalled)
        {
            AppLog.Error($"Tried to start \"{vm.Name}\" but QEMU isn't installed at {QemuExe}.");
            throw new Exception("QEMU isn't installed yet.");
        }

        if (!File.Exists(vm.DiskPath))
        {
            if (vm.ImportedDisk)
            {
                AppLog.Error($"\"{vm.Name}\" points at an imported disk that no longer exists: {vm.DiskPath}");
                throw new Exception($"The imported disk file is missing:\n{vm.DiskPath}\n\nIt may have been moved or deleted outside QVM Manager.");
            }
            AppLog.Info($"No disk yet for \"{vm.Name}\", creating {vm.DiskSizeGb}G {vm.DiskFormat} at {vm.DiskPath}");
            CreateDisk(vm.DiskPath, vm.DiskSizeGb, vm.DiskFormat);
        }

        var whpx = vm.EnableAcceleration && IsWhpxAvailable();
        AppLog.Info($"Starting \"{vm.Name}\": acceleration={(vm.EnableAcceleration ? (whpx ? "WHPX" : "requested but unavailable -> TCG") : "disabled -> TCG")}");

        var psi = new ProcessStartInfo
        {
            FileName = QemuExe,
            UseShellExecute = false,
            WorkingDirectory = vm.FolderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true, // hides the console window; the VM's own display window still opens
        };
        var args = psi.ArgumentList;

        args.Add("-machine"); args.Add(string.IsNullOrWhiteSpace(vm.MachineType) ? "q35" : vm.MachineType);

        args.Add("-accel"); args.Add(whpx ? "whpx,kernel-irqchip=off" : "tcg");
        args.Add("-cpu"); args.Add(BuildCpuArg(vm, whpx));

        args.Add("-m"); args.Add(vm.RamMb.ToString());
        args.Add("-smp"); args.Add(vm.Cpus.ToString());

        AddDiskArgs(args, vm);

        args.Add("-nic"); args.Add($"user,model={vm.NetworkModel}");
        args.Add("-vga"); args.Add(vm.DisplayDevice);

        // -g (initial resolution) only takes effect with the std/vmware/cirrus VGA devices,
        // not virtio or qxl - those negotiate resolution with the guest driver instead.
        if (!string.IsNullOrWhiteSpace(vm.Resolution) &&
            (vm.DisplayDevice == "std" || vm.DisplayDevice == "vmware"))
        {
            args.Add("-g"); args.Add(vm.Resolution);
        }

        if (vm.EnableAudio)
        {
            args.Add("-audiodev"); args.Add("dsound,id=snd0");
            args.Add("-device"); args.Add("intel-hda");
            args.Add("-device"); args.Add("hda-duplex,audiodev=snd0");
        }

        args.Add("-usb");
        if (vm.AttachTabletDevice)
        {
            args.Add("-device"); args.Add("usb-tablet");
        }

        if (vm.BootFromIsoOnce && !string.IsNullOrWhiteSpace(vm.IsoPath) && File.Exists(vm.IsoPath))
        {
            args.Add("-cdrom"); args.Add(vm.IsoPath);
            args.Add("-boot"); args.Add("order=c,once=d");
        }

        foreach (var extra in SplitArgs(vm.ExtraArgs))
            args.Add(extra);

        AppLog.Info($"Command line for \"{vm.Name}\": {QemuExe} {string.Join(" ", args.Select(QuoteIfNeeded))}");

        var stderrBuffer = new System.Text.StringBuilder();

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data); };
        proc.Exited += (_, _) =>
        {
            _running.Remove(vm.Id);
            var exitCode = -1;
            try { exitCode = proc.ExitCode; } catch { /* ignore */ }

            string stderrText;
            lock (stderrBuffer) stderrText = stderrBuffer.ToString();

            if (exitCode == 0)
                AppLog.Info($"\"{vm.Name}\" exited normally (code 0).");
            else
                AppLog.Error($"\"{vm.Name}\" exited with code {exitCode}.{(string.IsNullOrWhiteSpace(stderrText) ? "" : "\nQEMU stderr:\n" + stderrText)}");

            VmStopped?.Invoke(vm);
        };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to launch QEMU for \"{vm.Name}\".", ex);
            throw;
        }

        _running[vm.Id] = proc;
        vm.LastStartedAt = DateTime.Now;
    }

    /// <summary>
    /// Builds the -cpu value: model (blank = auto: "host" under WHPX, "max" under TCG) plus any
    /// user flags, so "Haswell" + "-svm -hle,-rtm" becomes "Haswell,-svm,-hle,-rtm".
    /// </summary>
    public static string BuildCpuArg(VirtualMachine vm, bool whpx)
    {
        var model = string.IsNullOrWhiteSpace(vm.CpuModel) ? (whpx ? "host" : "max") : vm.CpuModel.Trim();

        var flags = (vm.CpuFlags ?? "")
            .Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .ToList();

        if (whpx && !string.IsNullOrWhiteSpace(vm.CpuModel) && !vm.CpuModel.Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
            AppLog.Warn($"\"{vm.Name}\": using CPU model \"{model}\" under WHPX - named/masked models can hang under WHPX; clear the CPU model if it won't boot.");

        return flags.Count == 0 ? model : model + "," + string.Join(",", flags);
    }

    /// <summary>How many VMs this app launched are still running.</summary>
    public int RunningCount => _running.Values.Count(p => { try { return !p.HasExited; } catch { return false; } });

    /// <summary>Force-stops every VM this app launched (used when the app closes).</summary>
    public void KillAll()
    {
        foreach (var p in _running.Values.ToList())
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    private static string QuoteIfNeeded(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    /// <summary>Builds the right -drive/-device combination for the chosen disk interface and format.</summary>
    private static void AddDiskArgs(System.Collections.Generic.IList<string> args, VirtualMachine vm)
    {
        var format = string.IsNullOrWhiteSpace(vm.DiskFormat) ? "qcow2" : vm.DiskFormat;

        switch (vm.DiskInterface)
        {
            case "ide":
                args.Add("-drive"); args.Add($"file={vm.DiskPath},if=ide,format={format}");
                break;

            case "sata":
                args.Add("-drive"); args.Add($"file={vm.DiskPath},if=none,id=sata_disk0,format={format}");
                args.Add("-device"); args.Add("ahci,id=ahci0");
                args.Add("-device"); args.Add("ide-hd,drive=sata_disk0,bus=ahci0.0");
                break;

            case "nvme":
                args.Add("-drive"); args.Add($"file={vm.DiskPath},if=none,id=nvme_disk0,format={format}");
                args.Add("-device"); args.Add("nvme,drive=nvme_disk0,serial=qvm0001");
                break;

            case "virtio":
            default:
                // discard/detect-zeroes=unmap need qcow2's own sparse-file support; an imported
                // raw/vdi/vmdk disk doesn't get those flags.
                var extra = format == "qcow2" ? ",discard=unmap,detect-zeroes=unmap" : "";
                args.Add("-drive"); args.Add($"file={vm.DiskPath},if=virtio,format={format}{extra}");
                break;
        }
    }

    /// <summary>Splits a user-typed extra-args string into tokens, honoring "quoted, spaced" values.</summary>
    private static System.Collections.Generic.IEnumerable<string> SplitArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    public void Stop(VirtualMachine vm, bool force = false)
    {
        if (!_running.TryGetValue(vm.Id, out var proc) || proc.HasExited) return;

        if (force)
        {
            proc.Kill();
        }
        else
        {
            // QEMU on Windows has no clean "ACPI shutdown" signal from outside without the
            // QMP monitor wired up; closing the window is the normal path. As a script-driven
            // fallback, ask nicely first, then force after a short grace period.
            try { proc.CloseMainWindow(); } catch { /* ignore */ }
        }
    }
}
