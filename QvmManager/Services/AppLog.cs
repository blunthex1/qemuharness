using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QvmManager.Services;

/// <summary>
/// A tiny rolling log, always at %APPDATA%\QvmManager\logs\qvm.log regardless of where the
/// user points the VM storage - so it's always findable and survives a storage-location move.
/// Every QEMU launch (with its exact command line), install step, and caught exception gets
/// written here so problems can be diagnosed from the log/DiagnosticsWindow instead of back-and-forth.
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB, then trim to the newest half

    public static string LogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QvmManager", "logs", "qvm.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level,-5} {message}{Environment.NewLine}";
                File.AppendAllText(LogFile, line);
                TrimIfNeeded();
            }
        }
        catch
        {
            // Logging must never be why the app crashes.
        }
    }

    private static void TrimIfNeeded()
    {
        var info = new FileInfo(LogFile);
        if (!info.Exists || info.Length <= MaxBytes) return;

        var lines = File.ReadAllLines(LogFile);
        var keep = lines.Skip(lines.Length / 2).ToArray();
        File.WriteAllLines(LogFile, keep);
    }

    public static string ReadTail(int maxLines = 400)
    {
        try
        {
            if (!File.Exists(LogFile)) return "(no log yet)";
            var lines = File.ReadAllLines(LogFile);
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)));
        }
        catch (Exception ex)
        {
            return $"(couldn't read log: {ex.Message})";
        }
    }
}
