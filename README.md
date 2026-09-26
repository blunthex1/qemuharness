# QVM Manager

A lightweight VM manager for Windows, in the spirit of VirtualBox/VMware Workstation,
built on top of QEMU (open-source, no account, no Oracle/Broadcom involved).

- Create and manage multiple VMs from a list, like a real hypervisor GUI
- Set RAM, CPU cores, disk size, ISO, audio per VM
- Start/Stop from the app
- Auto-downloads and installs QEMU on first run (from the official qemu.weilnetz.de builds)
- Uses Windows Hypervisor Platform (WHPX) acceleration automatically when it's enabled,
  falls back to software emulation (TCG) if not
- All VM files live in `Documents\QvmManager\VMs\<vm name>\`

## Known limitation: no GPU acceleration

This app is a QEMU front-end, so it inherits QEMU/WHPX's limits: **WHPX does not support
3D/GPU passthrough to the guest.** Guests get a 2D framebuffer (`-vga std`) — fine for
general desktop use, dev work, Xcode/iOS Simulator, etc., but not for gaming or GPU compute
in the VM. Real GPU acceleration would require VFIO passthrough with a second physical GPU,
which is a much bigger, more fragile project on a Windows host and isn't wired up here.

## Building it

You'll need **Visual Studio 2022** (Community is free) with the ".NET desktop development"
workload, or just the **.NET 8 SDK** if you prefer the command line.

### Option A — Visual Studio
1. Open `QvmManager.sln`
2. Press F5 (or Ctrl+F5 to run without debugging)

### Option B — command line
```
cd QvmManager
dotnet build -c Release
dotnet run -c Release
```

### Option C — build a standalone .exe you can copy anywhere
```
cd QvmManager
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
The .exe will land in `QvmManager\bin\Release\net8.0-windows\win-x64\publish\`.

## If something goes wrong

Click **Diagnostics...** in the sidebar. It shows:
- System info (OS, .NET version, whether WHPX is on, QEMU version/path)
- Every VM's full settings, and whether its disk/ISO files actually exist on disk
- The last ~400 lines of the app's log, which includes the *exact* QEMU command line used
  for every launch and any error QEMU itself printed

There's a **Copy full report** button that puts all three sections on the clipboard as plain
text — paste that straight into a chat instead of describing/screenshotting the problem.
The raw log file always lives at `%APPDATA%\QvmManager\logs\qvm.log` regardless of where you
point the VM storage, so it survives a storage-location move.

## Notes

- **Run as a normal user is fine** — unlike the old PowerShell script, this app does not
  require "Run as Administrator." (If you ever hit a permissions issue enabling WHPX itself,
  that one-time step still needs an admin PowerShell: `Enable-WindowsOptionalFeature -Online
  -FeatureName HypervisorPlatform`, then restart.)
- First launch will offer to download QEMU (~70 MB) automatically. If you'd rather do it
  yourself, install QEMU into: `Documents\QvmManager\qemu\` (the app looks for
  `qemu-system-x86_64.exe` and `qemu-img.exe` there).
- Windows guests are heavier — the "New VM" dialog bumps default RAM/CPU up automatically
  when you pick "Windows" as the guest type, but you can always change it in the VM's
  settings panel afterward.
- There's no VM snapshots, cloning, or shared folders yet — this is the core "create,
  configure, start/stop" loop. Happy to add any of those next if you want them.
