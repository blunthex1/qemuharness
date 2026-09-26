using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QvmManager.Models;

namespace QvmManager.Services;

/// <summary>
/// Reads/writes the list of known VMs to a JSON file under the app root, and owns the
/// "VMs" subfolder where each VM's own folder (disk, etc.) lives. The app root itself
/// (and therefore this whole library) can be relocated to any drive/folder - see
/// <see cref="MoveTo"/>.
/// </summary>
public class VmLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string AppRoot { get; private set; }
    public string VmsFolder => Path.Combine(AppRoot, "VMs");
    private string LibraryFile => Path.Combine(AppRoot, "vms.json");

    public List<VirtualMachine> Vms { get; private set; } = new();

    public VmLibrary(string appRoot)
    {
        AppRoot = appRoot;
        Directory.CreateDirectory(VmsFolder);
        Load();
    }

    public void Load()
    {
        if (!File.Exists(LibraryFile))
        {
            Vms = new List<VirtualMachine>();
            return;
        }

        try
        {
            var json = File.ReadAllText(LibraryFile);
            Vms = JsonSerializer.Deserialize<List<VirtualMachine>>(json, JsonOptions) ?? new List<VirtualMachine>();
        }
        catch
        {
            // Corrupt file: back it up rather than losing it silently, start fresh in memory.
            var backup = LibraryFile + ".bak-" + DateTime.Now.Ticks;
            try { File.Copy(LibraryFile, backup, overwrite: true); } catch { /* best effort */ }
            Vms = new List<VirtualMachine>();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppRoot);
        var json = JsonSerializer.Serialize(Vms, JsonOptions);
        File.WriteAllText(LibraryFile, json);
    }

    public VirtualMachine CreateNew(string name)
    {
        var safeName = MakeSafeFolderName(name);
        var folder = Path.Combine(VmsFolder, safeName);

        // Avoid collisions with an existing folder of the same name.
        var attempt = folder;
        int i = 2;
        while (Directory.Exists(attempt))
        {
            attempt = folder + " (" + i + ")";
            i++;
        }
        folder = attempt;
        Directory.CreateDirectory(folder);

        var vm = new VirtualMachine
        {
            Name = name,
            FolderPath = folder,
            DiskPath = Path.Combine(folder, "disk.qcow2")
        };

        Vms.Add(vm);
        Save();
        return vm;
    }

    public void Remove(VirtualMachine vm, bool deleteFiles)
    {
        Vms.RemoveAll(v => v.Id == vm.Id);
        Save();

        if (deleteFiles && Directory.Exists(vm.FolderPath))
        {
            try { Directory.Delete(vm.FolderPath, recursive: true); }
            catch (Exception ex)
            {
                throw new IOException($"VM removed from the list, but its files couldn't be deleted:\n{ex.Message}");
            }
        }
    }

    /// <summary>
    /// Moves the whole library (every VM's files, the qemu install, and vms.json) to a new
    /// app root, which can be on a different drive. Rewrites every VM's FolderPath/DiskPath
    /// (and IsoPath, if it pointed inside the old root) to match. Call this instead of just
    /// re-pointing AppRoot when the user wants their existing VMs to come along.
    /// </summary>
    public void MoveTo(string newAppRoot, IProgress<string>? progress = null)
    {
        var oldAppRoot = Path.GetFullPath(AppRoot).TrimEnd('\\');
        newAppRoot = Path.GetFullPath(newAppRoot).TrimEnd('\\');

        if (string.Equals(oldAppRoot, newAppRoot, StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(newAppRoot);

        // qemu install (if any) and the VMs folder both live directly under AppRoot.
        MoveDirectoryContents(oldAppRoot, newAppRoot, progress);

        // Rewrite any path that pointed inside the old root.
        foreach (var vm in Vms)
        {
            vm.FolderPath = RemapPath(vm.FolderPath, oldAppRoot, newAppRoot);
            vm.DiskPath = RemapPath(vm.DiskPath, oldAppRoot, newAppRoot);
            if (!string.IsNullOrWhiteSpace(vm.IsoPath))
                vm.IsoPath = RemapPath(vm.IsoPath, oldAppRoot, newAppRoot);
        }

        AppRoot = newAppRoot;
        Save();

        // Best-effort cleanup of the now-empty old root.
        try
        {
            if (Directory.Exists(oldAppRoot) && !Directory.EnumerateFileSystemEntries(oldAppRoot).Any())
                Directory.Delete(oldAppRoot);
        }
        catch { /* not worth failing the whole move over */ }
    }

    private static string RemapPath(string path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var full = Path.GetFullPath(path);
        if (full.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase))
            return newRoot + full.Substring(oldRoot.Length);
        return path; // outside the old root (e.g. a user-picked ISO elsewhere) - leave it alone
    }

    private static void MoveDirectoryContents(string sourceRoot, string destRoot, IProgress<string>? progress)
    {
        if (!Directory.Exists(sourceRoot)) return;

        foreach (var dir in Directory.GetDirectories(sourceRoot))
        {
            var name = Path.GetFileName(dir);
            var dest = Path.Combine(destRoot, name);
            progress?.Report($"Moving {name}...");
            MoveOrCopyDirectory(dir, dest);
        }

        foreach (var file in Directory.GetFiles(sourceRoot))
        {
            var dest = Path.Combine(destRoot, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            File.Delete(file);
        }
    }

    private static void MoveOrCopyDirectory(string source, string dest)
    {
        try
        {
            // Fast path: same drive.
            Directory.Move(source, dest);
            return;
        }
        catch (IOException)
        {
            // Cross-drive (or dest already exists) - fall back to recursive copy + delete.
        }

        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            MoveOrCopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));

        Directory.Delete(source, recursive: true);
    }

    private static string MakeSafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "VM" : cleaned;
    }
}
