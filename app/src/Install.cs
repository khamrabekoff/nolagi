// Nolagi.exe - installation: the downloaded exe copies itself to Program Files, adds Start menu and desktop
// shortcuts and an entry in «Установленные приложения». C# 5 / .NET Framework 4.8.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace Pckit
{
    public static class Install
    {
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Nolagi";

        // Program Files: only administrators can write there, so nobody can swap the exe we run elevated
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nolagi"); } }
        public static string Exe { get { return Path.Combine(Dir, "Nolagi.exe"); } }
        static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Nolagi.lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Nolagi.lnk"); } }

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); }
        }

        /// Returns true when the installed copy was started and this process should exit.
        /// Any failure falls back to running from where the user started it (as before).
        public static bool EnsureInstalled(string[] args)
        {
            try
            {
                if (!SafeDir.Elevated) return false;                                                     // dev build (asInvoker)
                if (Path.GetFileName(Self).IndexOf("-dev", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (args.Contains("/portable")) return false;
                if (IsInstalledCopy) { Register(false); return false; }

                bool fresh = !File.Exists(Exe);
                bool copy = fresh;
                if (!fresh)
                {
                    Version have, mine = new Version(App.Version);
                    try { have = new Version(FileVersionInfo.GetVersionInfo(Exe).FileVersion); } catch { have = new Version(0, 0); }
                    copy = mine > have;                                       // an older download never replaces a newer install
                }
                if (copy)
                {
                    if (InstalledRunning()) return false;                     // can't overwrite a running exe: run this copy once
                    Directory.CreateDirectory(Dir);
                    string tmp = Exe + ".new";
                    File.Copy(Self, tmp, true);
                    if (File.Exists(Exe)) File.Delete(Exe);
                    File.Move(tmp, Exe);
                }
                Register(fresh || !File.Exists(StartLnk));
                Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Dir });
                return true;
            }
            catch (Exception ex)
            {
                Telemetry.Error("install", ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// Этот exe — скачанная версия новее установленной (а установленная, видимо, сейчас открыта).
        public static bool IsNewerDownload
        {
            get
            {
                try
                {
                    if (!SafeDir.Elevated || IsInstalledCopy || !File.Exists(Exe)) return false;
                    if (Path.GetFileName(Self).IndexOf("-dev", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    return new Version(App.Version) > new Version(FileVersionInfo.GetVersionInfo(Exe).FileVersion);
                }
                catch { return false; }
            }
        }

        static bool InstalledRunning()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("Nolagi"))
            {
                try { if (p.Id != me && string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
        }

        /// Uninstall entry (kept in sync with the version after self-updates) and, on install, the shortcuts.
        static void Register(bool shortcuts)
        {
            if (shortcuts)
            {
                try { Shortcut(StartLnk); } catch { }
                try { Shortcut(DesktopLnk); } catch { }
            }
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Nolagi");
                k.SetValue("DisplayVersion", App.Version);
                k.SetValue("Publisher", "odilbek");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try { k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord); } catch { }
                if (k.GetValue("InstallDate") == null) k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        static void Shortcut(string lnk)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Exe });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Dir });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { Exe + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Nolagi — настройка ПК для игр" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(sh); }
        }

        /// «Удалить» from Windows settings runs Nolagi.exe /uninstall.
        public static void Uninstall()
        {
            var answer = MessageBox.Show(
                "Удалить Nolagi с компьютера?\n\n" +
                "Настройки Windows, которые вы применили, останутся. Чтобы вернуть всё как было, нажмите «Нет», " +
                "откройте Nolagi → «Откат» → «Вернуть всё», а потом удалите программу.\n\n" +
                "Журнал отката сохраняется: если поставите Nolagi снова, отменить изменения можно будет и тогда.",
                "Удаление Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            foreach (var l in new[] { StartLnk, DesktopLnk }) { try { if (File.Exists(l)) File.Delete(l); } catch { } }
            Autostart.Remove();
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // scripts unpacked from the exe; the undo journal lives in %ProgramData%\pckit and is kept
            try
            {
                string scripts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nolagi");
                if (Directory.Exists(scripts)) Directory.Delete(scripts, true);
            }
            catch { }

            // the running exe can't delete itself: a hidden PowerShell waits for us to exit, then removes the folder
            int pid = Process.GetCurrentProcess().Id;
            string dir = Path.GetDirectoryName(Self);
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase))
            {
                string script =
                    "while (Get-Process -Id " + pid + " -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }\n" +
                    "Remove-Item -LiteralPath '" + Dir.Replace("'", "''") + "' -Recurse -Force -ErrorAction SilentlyContinue\n";
                string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + enc)
                { CreateNoWindow = true, UseShellExecute = false });
            }
            MessageBox.Show("Nolagi удалён.", "Удаление Nolagi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
