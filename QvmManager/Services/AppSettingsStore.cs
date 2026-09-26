using System;
using System.IO;
using System.Text.Json;

namespace QvmManager.Services;

public class AppSettings
{
    /// <summary>
    /// The top-level folder that holds everything: "<AppRoot>\VMs\<vm name>\..." and
    /// "<AppRoot>\qemu\". Null until the user has picked (or accepted the default) once.
    /// </summary>
    public string? AppRoot { get; set; }
}

/// <summary>
/// Stores just the "where is everything kept" setting. This file itself always lives in
/// %APPDATA%\QvmManager - it's tiny and has to be findable regardless of which drive the
/// user picks for the actual VM storage.
/// </summary>
public class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QvmManager");

    private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public AppSettingsStore()
    {
        Load();
    }

    public void Load()
    {
        if (!File.Exists(SettingsFile))
        {
            Settings = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, JsonOptions));
    }

    public static string DefaultAppRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QvmManager");
}
