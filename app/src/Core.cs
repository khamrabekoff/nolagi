// pckit.exe - core: payload extraction, engine bridge, settings, telemetry. C# 5 / .NET Framework 4.8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// the server reads this from the uploaded exe and refuses a release whose number doesn't match
[assembly: AssemblyFileVersion(Pckit.App.Version)]
[assembly: AssemblyProduct("Nolagi")]

namespace Pckit
{
    public static class J
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static Dictionary<string, object> Parse(string s)
        {
            return Ser.DeserializeObject(s) as Dictionary<string, object>;
        }
        public static string Write(object o) { return Ser.Serialize(o); }

        public static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            return (d != null && d.TryGetValue(k, out v)) ? v : null;
        }
        public static string S(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        public static bool B(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            return v is bool && (bool)v;
        }
        public static double N(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            if (v == null) return 0;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }
        public static Dictionary<string, object> O(Dictionary<string, object> d, string k)
        {
            return Get(d, k) as Dictionary<string, object>;
        }
        public static List<Dictionary<string, object>> A(Dictionary<string, object> d, string k)
        {
            var res = new List<Dictionary<string, object>>();
            var arr = Get(d, k) as object[];
            if (arr == null)
            {
                var one = Get(d, k) as Dictionary<string, object>;
                if (one != null) res.Add(one);
                return res;
            }
            foreach (var x in arr) { var dx = x as Dictionary<string, object>; if (dx != null) res.Add(dx); }
            return res;
        }
    }

    public static class Hash
    {
        public static string Sha256(Stream s)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }
        public static string Sha256(byte[] b) { using (var ms = new MemoryStream(b)) return Sha256(ms); }
        public static string Sha256File(string path) { using (var f = File.OpenRead(path)) return Sha256(f); }
    }

    /// Where Nolagi keeps the scripts it runs as administrator. Elevated: %ProgramData%\Nolagi, locked so that
    /// only Administrators/SYSTEM can write (a non-admin process must not be able to swap code we later run elevated).
    public static class SafeDir
    {
        public static bool Elevated
        {
            get { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        }

        public static string Base
        {
            get
            {
                if (!Elevated) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nolagi"); // dev build only
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nolagi");
                Directory.CreateDirectory(dir);
                Lock(dir);
                return dir;
            }
        }

        // owner = Administrators; SYSTEM/Administrators full control, Users read+execute; inheritance from ProgramData cut off
        static void Lock(string dir)
        {
            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            var inh = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            var prop = System.Security.AccessControl.PropagationFlags.None;
            var acl = new System.Security.AccessControl.DirectorySecurity();
            acl.SetOwner(admins);
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admins, System.Security.AccessControl.FileSystemRights.FullControl, inh, prop, System.Security.AccessControl.AccessControlType.Allow));
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(system, System.Security.AccessControl.FileSystemRights.FullControl, inh, prop, System.Security.AccessControl.AccessControlType.Allow));
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users, System.Security.AccessControl.FileSystemRights.ReadAndExecute, inh, prop, System.Security.AccessControl.AccessControlType.Allow));
            Directory.SetAccessControl(dir, acl);
        }
    }

    /// Unpacks engine + data + bridge from the exe into <SafeDir>\app-<hash>, and re-checks every file on each start.
    public static class Payload
    {
        public static string Root;

        public static void Extract()
        {
            var asm = Assembly.GetExecutingAssembly();
            byte[] zip;
            using (var s = asm.GetManifestResourceStream("payload.zip"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                zip = ms.ToArray();
            }
            string baseDir = SafeDir.Base;
            Root = Path.Combine(baseDir, "app-" + Hash.Sha256(zip).Substring(0, 10));
            if (!Intact(zip))
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
                Directory.CreateDirectory(Root);
                using (var ms = new MemoryStream(zip))
                using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
                    za.ExtractToDirectory(Root);
            }
            // old versions are not needed
            try
            {
                foreach (var d in Directory.GetDirectories(baseDir, "app-*"))
                    if (!string.Equals(d, Root, StringComparison.OrdinalIgnoreCase)) { try { Directory.Delete(d, true); } catch { } }
            }
            catch { }
        }

        // every file on disk must match the copy inside the exe
        static bool Intact(byte[] zip)
        {
            if (!Directory.Exists(Root)) return false;
            using (var ms = new MemoryStream(zip))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var en in za.Entries)
                {
                    if (string.IsNullOrEmpty(en.Name)) continue;
                    string path = Path.Combine(Root, en.FullName.Replace('/', '\\'));
                    if (!File.Exists(path) || new FileInfo(path).Length != en.Length) return false;
                    string want;
                    using (var s = en.Open()) want = Hash.Sha256(s);
                    if (Hash.Sha256File(path) != want) return false;
                }
            }
            return true;
        }
    }

    /// Runs one bridge.ps1 command in a hidden PowerShell and streams its JSON lines.
    public static class Bridge
    {
        public static Task<Dictionary<string, object>> Run(string cmd, IEnumerable<string> ids, Action<Dictionary<string, object>> onMsg)
        {
            return Task.Run(() =>
            {
                string script = Path.Combine(Payload.Root, "app", "bridge.ps1");
                string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Cmd " + cmd;
                // ids go through a JSON file: startup entry names may contain commas, quotes, any characters
                string idsFile = null;
                if (ids != null)
                {
                    idsFile = Path.Combine(Payload.Root, "ids-" + Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(idsFile, J.Write(ids.ToArray()), new UTF8Encoding(false));
                    args += " -IdsFile \"" + idsFile + "\"";
                }
                try { return RunProcess(args, onMsg); }
                finally { if (idsFile != null) try { File.Delete(idsFile); } catch { } }
            });
        }

        static Dictionary<string, object> RunProcess(string args, Action<Dictionary<string, object>> onMsg)
        {
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    WorkingDirectory = Payload.Root
                };
                Dictionary<string, object> result = null;
                string error = null;
                var err = new StringBuilder();
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (!line.StartsWith("{")) continue;
                        Dictionary<string, object> m;
                        try { m = J.Parse(line); } catch { continue; }
                        if (m == null) continue;
                        string t = J.S(m, "t");
                        if (t == "result") result = m;
                        else if (t == "error") error = J.S(m, "msg");
                        else if (onMsg != null) onMsg(m);
                    }
                    p.WaitForExit();
                }
                if (error != null) throw new Exception(error);
                if (result == null)
                {
                    string e = err.ToString().Trim();
                    throw new Exception(e.Length > 0 ? e : "движок не ответил");
                }
                return result;
            }
        }
    }

    public class Settings
    {
        public string Id = "";
        public string Nick = "";
        public bool Consent;
        public bool Asked;
        public List<int> SeenNews = new List<int>();
        public bool OverlayOn;
        public int OverlayCorner = 1;   // 0 слева сверху, 1 справа сверху, 2 слева снизу, 3 справа снизу

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nolagi", "client.json"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = J.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                    s.Id = J.S(d, "id"); s.Nick = J.S(d, "nick"); s.Consent = J.B(d, "consent"); s.Asked = J.B(d, "asked");
                    s.OverlayOn = J.B(d, "overlay");
                    if (J.Get(d, "overlayCorner") != null) s.OverlayCorner = Math.Max(0, Math.Min(3, (int)J.N(d, "overlayCorner")));
                    var seen = J.Get(d, "seenNews") as object[];
                    if (seen != null) foreach (var x in seen) { try { s.SeenNews.Add(Convert.ToInt32(x)); } catch { } }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString();
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, J.Write(new Dictionary<string, object> { { "id", Id }, { "nick", Nick }, { "consent", Consent }, { "asked", Asked }, { "seenNews", SeenNews.ToArray() }, { "overlay", OverlayOn }, { "overlayCorner", OverlayCorner } }), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// Anonymous usage report to the owner's server - only with the user's consent.
    public static class Telemetry
    {
        public const string Base = "https://api.nolagi.dpdns.org";
        // прежний адрес: сервер мог выдать ссылку на скачивание с него (его ещё слушают копии 1.0.0)
        public const string OldBase = "https://158-101-202-7.sslip.io";
        public static string Key = "";

        // домен бесплатный: если он перестанет открываться, программа всё равно дойдёт до сервера по старому адресу
        static T WithFallback<T>(Func<string, T> call)
        {
            try { return call(Base); }
            catch (WebException) { return call(OldBase); }
        }
        public static Settings Cfg;
        public static Dictionary<string, object> Hw;

        public static void Send(string evt, IEnumerable<KeyValuePair<string, bool>> tweaks)
        {
            if (Cfg == null || !Cfg.Consent || string.IsNullOrEmpty(Key)) return;
            var body = new Dictionary<string, object>
            {
                { "client_id", Cfg.Id }, { "nick", Cfg.Nick }, { "version", App.Version }, { "event", evt }
            };
            if (Hw != null)
                body["hw"] = new Dictionary<string, object> {
                    { "cpu", J.S(Hw, "cpu") }, { "gpu", J.S(Hw, "gpu") }, { "ram_gb", J.N(Hw, "ramGb") }, { "os", J.S(Hw, "os") } };
            if (tweaks != null)
                body["tweaks"] = tweaks.Select(t => (object)new Dictionary<string, object> { { "id", t.Key }, { "ok", t.Value } }).ToArray();
            string json = J.Write(body);
            Task.Run(() => { try { WithFallback(b => Post(b + "/api/v1/report", json)); } catch { } });
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers["X-Pckit-Key"] = Key;
            wc.Headers[HttpRequestHeader.UserAgent] = "Nolagi/" + App.Version;
            return wc;
        }

        static string Post(string url, string json)
        {
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                return wc.UploadString(url, json);
            }
        }

        /// What went wrong on a user's PC: shows up in the panel's «Ошибки» tab. Only with consent.
        /// One request per operation (all failed items joined), so a bad run can't eat the server's rate limit.
        public static void Error(string where, string message)
        {
            if (Cfg == null || !Cfg.Consent || string.IsNullOrEmpty(Key) || string.IsNullOrEmpty(message)) return;
            string json = J.Write(new Dictionary<string, object> {
                { "client_id", Cfg.Id }, { "version", App.Version }, { "where", where }, { "message", message.Length > 1900 ? message.Substring(0, 1900) : message } });
            Task.Run(() => { try { WithFallback(b => Post(b + "/api/v1/error", json)); } catch { } });
        }

        /// «Написать автору»: an explicit action of the user, so it is sent without the stats consent.
        public static Task Feedback(string text)
        {
            string json = J.Write(new Dictionary<string, object> {
                { "client_id", Cfg.Id }, { "nick", Cfg.Nick }, { "version", App.Version }, { "text", text } });
            return Task.Run(() => { WithFallback(b => Post(b + "/api/v1/feedback", json)); });
        }

        /// New version + announcements. Sends only the version number.
        public static Task<Dictionary<string, object>> CheckUpdate()
        {
            return Task.Run(() =>
            {
                try
                {
                    return WithFallback(b =>
                    {
                        using (var wc = Client())
                            return J.Parse(wc.DownloadString(b + "/api/v1/update?v=" + Uri.EscapeDataString(App.Version)));
                    });
                }
                catch { return null; }
            });
        }
    }

    /// One-click update: download, check SHA-256, swap the exe after this process exits, start it again.
    /// The file lives in the admin-only folder and its hash is checked again right before it is started.
    public static class Updater
    {
        public static Task<string> Download(string url, string sha256, Action<int> progress)
        {
            if (url != null && url.StartsWith("/download/", StringComparison.Ordinal)) url = Telemetry.Base + url;   // относительная ссылка
            if (url == null || !(url.StartsWith(Telemetry.Base + "/download/", StringComparison.OrdinalIgnoreCase) ||
                                 url.StartsWith(Telemetry.OldBase + "/download/", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("адрес обновления не с сервера Nolagi");
            string dir = Path.Combine(SafeDir.Base, "update");
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "Nolagi-update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            var tcs = new TaskCompletionSource<string>();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Nolagi/" + App.Version;
            wc.DownloadProgressChanged += (s, e) => { if (progress != null) progress(e.ProgressPercentage); };
            wc.DownloadFileCompleted += (s, e) =>
            {
                wc.Dispose();
                if (e.Error != null) { tcs.SetException(e.Error); return; }
                try
                {
                    string got = Hash.Sha256File(tmp);
                    if (!string.Equals(got, (sha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tmp); } catch { }
                        tcs.SetException(new Exception("файл обновления повреждён (контрольная сумма не совпала)"));
                        return;
                    }
                    tcs.SetResult(tmp);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            };
            wc.DownloadFileAsync(new Uri(url), tmp);
            return tcs.Task;
        }

        // PowerShell via -EncodedCommand (UTF-16): any path works (Cyrillic, spaces, %, quotes); no script file on disk
        public static void InstallAndRestart(string newExe, string sha256)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int pid = Process.GetCurrentProcess().Id;
            Func<string, string> q = s => "'" + s.Replace("'", "''") + "'";
            string script =
                "$ErrorActionPreference='Stop'\n" +
                "while (Get-Process -Id " + pid + " -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }\n" +
                "$new=" + q(newExe) + "; $exe=" + q(exe) + "\n" +
                "if ((Get-FileHash -LiteralPath $new -Algorithm SHA256).Hash -ne " + q(sha256.Trim().ToUpperInvariant()) + ") { Remove-Item -LiteralPath $new -Force; Start-Process -FilePath $exe; exit 1 }\n" +
                "try { Move-Item -LiteralPath $new -Destination $exe -Force } catch { Start-Sleep 2; Move-Item -LiteralPath $new -Destination $exe -Force }\n" +
                "Start-Process -FilePath $exe\n";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + enc)
            { CreateNoWindow = true, UseShellExecute = false });
        }
    }

    public static class Logo
    {
        // N whose diagonal is a lightning bolt (120x120 design grid)
        public const string Data = "M32,92 L32,28 L66,60 L52,64 L88,92 L88,28";
    }
}
