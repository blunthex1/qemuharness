using System;
using System.Windows;
using System.Windows.Threading;
using QvmManager.Services;

namespace QvmManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Info($"=== QVM Manager starting (OS: {Environment.OSVersion}, .NET: {Environment.Version}) ===");

        // Catch anything that would otherwise just crash silently, log it, and let the person
        // see what happened instead of the app vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"QVM Manager hit an unexpected error:\n\n{args.Exception.Message}\n\n" +
                $"Details were written to the log ({AppLog.LogFile}). Use the Diagnostics window to copy a full report.",
                "QVM Manager - error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error("Unhandled non-UI exception (fatal)", ex);
        };
    }
}
