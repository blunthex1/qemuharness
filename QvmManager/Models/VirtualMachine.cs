using System;
using System.Text.Json.Serialization;

namespace QvmManager.Models;

public enum GuestOsType
{
    Linux,
    WindowsGuest,
    Other
}

/// <summary>
/// Persisted configuration for one VM. Serialized to JSON in the library file.
/// Each VM gets its own folder (under the library root) holding its disk image
/// and any per-VM files.
/// </summary>
public class VirtualMachine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New VM";
    public GuestOsType OsType { get; set; } = GuestOsType.Linux;

    // Paths
    public string FolderPath { get; set; } = "";      // <library>\<vm name>\
    public string DiskPath { get; set; } = "";         // <FolderPath>\disk.qcow2
    public string IsoPath { get; set; } = "";           // installer ISO, optional after first boot
    public bool BootFromIsoOnce { get; set; } = true;   // once installed, user unchecks this

    // Hardware
    public int RamMb { get; set; } = 4096;
    public int Cpus { get; set; } = 2;
    public string DiskSizeGb { get; set; } = "40";      // only used when QVM Manager creates the disk itself
    public string DiskFormat { get; set; } = "qcow2";   // qcow2 | vdi | vmdk | vhdx | raw - matches whatever DiskPath actually is
    public bool ImportedDisk { get; set; } = false;     // true if DiskPath points at a disk the user already had, not one QVM Manager created
    public bool EnableAudio { get; set; } = true;
    public bool EnableAcceleration { get; set; } = true; // use WHPX if available

    // Chipset / devices - these map straight to QEMU's own -machine, -drive if=,
    // -vga and -nic model= options, so the values below are QEMU's own option names.
    public string MachineType { get; set; } = "q35";        // q35 | pc
    public string DiskInterface { get; set; } = "virtio";   // virtio | ide | sata | nvme
    public string DisplayDevice { get; set; } = "std";      // std | virtio | qxl | vmware
    public string Resolution { get; set; } = "";            // "" = default, else "WIDTHxHEIGHT" for -g (std/vmware only)
    public string NetworkModel { get; set; } = "virtio-net-pci"; // virtio-net-pci | e1000 | rtl8139
    public bool AttachTabletDevice { get; set; } = true;    // usb-tablet: smoother mouse capture

    /// <summary>
    /// Raw extra command-line arguments appended after everything else, split on whitespace
    /// (quote a value with spaces, e.g. -device "some,thing"). This is the escape hatch for any
    /// QEMU flag not covered by a dedicated field above - QEMU has hundreds of them.
    /// </summary>
    public string ExtraArgs { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastStartedAt { get; set; }

    /// <summary>Not persisted - set by the UI layer before refreshing the list display.</summary>
    [JsonIgnore]
    public string StatusText { get; set; } = "Stopped";
}
