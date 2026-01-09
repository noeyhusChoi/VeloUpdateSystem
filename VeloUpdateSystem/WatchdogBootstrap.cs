using System;
using System.IO;

namespace VeloUpdateSystem;

public static class WatchdogBootstrap
{
    private const string WatchdogExeName = "Watchdog.exe";
    private const string StartupShortcutName = "VeloUpdateSystem Watchdog.lnk";

    public static bool ShouldRunInstallTasks(string[] args)
    {
        return HasArg(args, "--veloapp-install") || HasArg(args, "--veloapp-updated");
    }

    public static void EnsureInstalled()
    {
        var sourceDir = Path.Combine(AppContext.BaseDirectory, "Watchdog");
        var sourceExe = Path.Combine(sourceDir, WatchdogExeName);
        if (!File.Exists(sourceExe))
        {
            return;
        }

        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeloUpdateSystem",
            "Watchdog");
        Directory.CreateDirectory(targetDir);

        CopyDirectory(sourceDir, targetDir);

        var targetExe = Path.Combine(targetDir, WatchdogExeName);
        if (File.Exists(targetExe))
        {
            CreateStartupShortcut(targetExe);
        }
    }

    private static bool HasArg(string[] args, string value)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void CreateStartupShortcut(string targetExe)
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupDir))
        {
            return;
        }

        var shortcutPath = Path.Combine(startupDir, StartupShortcutName);
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe);
            shortcut.WindowStyle = 1;
            shortcut.Save();
        }
        catch
        {
        }
    }
}
