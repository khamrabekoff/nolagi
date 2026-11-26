// Nolagi.exe - игровой режим без интерфейса: пока идёт игра, закрывает выбранные программы и понижает приоритет другим,
// после игры запускает закрытые обратно (с той же командой и с обычными правами) и возвращает приоритеты.
// Плюс очистка «ждущей» памяти во время игры (как ISLC) и автозапуск через планировщик. C# 5 / .NET Framework 4.8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Pckit
{
    public static class GameRules
    {
        public const string Close = "close", Low = "low", Keep = "keep";

        /// Игры, которые узнаются всегда. Свои игры пользователь добавляет сам.
        public static readonly Dictionary<string, string> BuiltInGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cs2.exe", "Counter-Strike 2" }, { "dota2.exe", "Dota 2" }
        };

        /// Эти процессы Nolagi не трогает никогда и не показывает в списке: они нужны игре, античитам или самой Windows.
        public static readonly HashSet<string> Locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe",
            "faceit.exe", "faceitclient.exe", "faceitservice.exe", "faceit_ac.exe",
            "easyanticheat.exe", "easyanticheat_eos.exe", "beservice.exe", "vgc.exe", "vgtray.exe",
            "nolagi.exe", "nolagi-dev.exe", "explorer.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "svchost.exe", "audiodg.exe",
            "ctfmon.exe", "sihost.exe", "taskhostw.exe", "runtimebroker.exe", "searchhost.exe", "searchapp.exe",
            "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "applicationframehost.exe",
            "systemsettings.exe", "lsass.exe", "conhost.exe", "dllhost.exe", "fontdrvhost.exe", "smss.exe", "wininit.exe",
            "services.exe", "securityhealthsystray.exe", "msmpeng.exe", "nvcontainer.exe", "nvdisplay.container.exe",
            "powershell.exe", "cmd.exe", "windowsterminal.exe", "openconsole.exe", "taskmgr.exe", "lockapp.exe"
        };

        /// Что предлагается по умолчанию. Всё остальное — «не трогать», пока пользователь не выберет сам.
        public static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // лаунчеры и облака: качают обновления и синхронизируют файлы прямо во время матча
            { "epicgameslauncher.exe", Close }, { "eadesktop.exe", Close }, { "upc.exe", Close }, { "battle.net.exe", Close },
            { "galaxyclient.exe", Close }, { "onedrive.exe", Close },
            // браузеры и торренты: остаются открытыми, но уступают процессор игре
            { "chrome.exe", Low }, { "msedge.exe", Low }, { "firefox.exe", Low }, { "browser.exe", Low }, { "opera.exe", Low },
            { "qbittorrent.exe", Low }, { "utorrent.exe", Low },
            // голосовой чат, запись и музыка нужны во время игры
            { "discord.exe", Keep }, { "obs64.exe", Keep }, { "telegram.exe", Keep }, { "spotify.exe", Keep }
        };

        public static string Get(IDictionary<string, string> user, string exe)
        {
            string r;
            if (user != null && user.TryGetValue(exe, out r)) return r;
            if (Defaults.TryGetValue(exe, out r)) return r;
            return Keep;
        }
    }

    /// Снимок настроек для фонового потока: интерфейс может менять настройки, пока идёт проверка.
    public class GameModeConfig
    {
        public HashSet<string> Games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool MemClean;
    }

    public class GameModeEngine
    {
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        readonly object sync = new object();
        static readonly int MySession = Process.GetCurrentProcess().SessionId;
        static readonly int MyPid = Process.GetCurrentProcess().Id;

        public string Game;        // exe игры, которая сейчас идёт (null — игры нет)
        int gamePid;
        DateTime since;
        readonly List<string> closedNames = new List<string>();
        readonly List<string> refusedNames = new List<string>();   // спросили «Сохранить?» — не трогаем
        readonly List<string> restart = new List<string>();                 // команды, которыми запустить закрытое обратно
        readonly Dictionary<int, Tuple<DateTime, ProcessPriorityClass>> lowered = new Dictionary<int, Tuple<DateTime, ProcessPriorityClass>>();
        int purges;
        DateTime lastPurge = DateTime.MinValue;

        /// Строка для интерфейса и подсказки значка в трее.
        public volatile string Status = "Ждёт запуска игры.";

        public void Tick(GameModeConfig c)
        {
            lock (sync)
            {
                Process[] all = Process.GetProcesses();
                try
                {
                    if (Game == null)
                    {
                        foreach (var p in all)
                        {
                            string exe = ExeName(p);
                            if (exe == null || !c.Games.Contains(exe) || SessionOf(p) != MySession) continue;
                            Begin(p, exe, all, c);
                            break;
                        }
                        if (Game == null) Status = "Ждёт запуска игры.";
                    }
                    else
                    {
                        Process g = all.FirstOrDefault(p => p.Id == gamePid);
                        if (g == null || !string.Equals(ExeName(g), Game, StringComparison.OrdinalIgnoreCase)) { EndLocked(true); return; }
                        LowerAll(all, c);      // браузер, открытый посреди матча, тоже уступает игре
                        if (lowered.Count != savedLowered) SavePending();
                        if (c.MemClean && MemPurge.Suits) MaybePurge();
                        Status = Describe();
                    }
                }
                finally { foreach (var p in all) p.Dispose(); }
            }
        }

        /// Игра закрылась или Nolagi выходит: всё возвращается. restartApps = false при выходе из Windows.
        public void End(bool restartApps)
        {
            lock (sync) EndLocked(restartApps);
        }

        void Begin(Process game, string exe, Process[] all, GameModeConfig c)
        {
            Game = exe; gamePid = game.Id; since = DateTime.Now;
            closedNames.Clear(); refusedNames.Clear(); restart.Clear(); lowered.Clear(); purges = 0;
            CloseApps(all, c);
            LowerAll(all, c);
            SavePending();
            Status = Describe();
        }

        // ---- если Nolagi упадёт или его закроют через диспетчер задач посреди игры, при следующем запуске всё вернётся

        static string PendingPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nolagi", "gamemode-pending.json"); } }
        int savedLowered = -1;

        void SavePending()
        {
            if (savedLowered == lowered.Count && restart.Count == 0 && lowered.Count == 0) return;
            savedLowered = lowered.Count;
            try
            {
                var low = lowered.Select(kv => (object)new Dictionary<string, object> {
                    { "pid", kv.Key }, { "start", kv.Value.Item1.ToBinary().ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "prio", (int)kv.Value.Item2 } }).ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
                File.WriteAllText(PendingPath, J.Write(new Dictionary<string, object> { { "restart", restart.ToArray() }, { "lowered", low } }), new UTF8Encoding(false));
            }
            catch { }
        }

        static void DropPending() { try { File.Delete(PendingPath); } catch { } }

        /// Вызывается при старте Nolagi: вернуть то, что не вернул упавший прошлый запуск.
        public static void RecoverAfterCrash()
        {
            try
            {
                if (!File.Exists(PendingPath)) return;
                var d = J.Parse(File.ReadAllText(PendingPath, Encoding.UTF8));
                DropPending();
                foreach (var l in J.A(d, "lowered"))
                {
                    try
                    {
                        var start = DateTime.FromBinary(long.Parse(J.S(l, "start"), System.Globalization.CultureInfo.InvariantCulture));
                        using (var p = Process.GetProcessById((int)J.N(l, "pid")))
                            if (p.StartTime == start && p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = (ProcessPriorityClass)(int)J.N(l, "prio");
                    }
                    catch { }
                }
                var cmds = J.Get(d, "restart") as object[];
                if (cmds != null) foreach (var c in cmds) { string s = Convert.ToString(c); if (!string.IsNullOrEmpty(s)) Unelevated.Start(s); }
            }
            catch { }
        }

        void EndLocked(bool restartApps)
        {
            if (Game == null) return;
            foreach (var kv in lowered)
            {
                try
                {
                    using (var p = Process.GetProcessById(kv.Key))
                        if (p.StartTime == kv.Value.Item1 && p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = kv.Value.Item2;
                }
                catch { }   // процесс уже закрыт
            }
            lowered.Clear();
            if (restartApps) foreach (var cmd in restart) Unelevated.Start(cmd);
            restart.Clear();
            DropPending(); savedLowered = -1;
            Game = null; gamePid = 0;
            Status = "Ждёт запуска игры.";
        }

        string Describe()
        {
            string name;
            if (!GameRules.BuiltInGames.TryGetValue(Game, out name)) name = Path.GetFileNameWithoutExtension(Game);
            var parts = new List<string>();
            if (closedNames.Count > 0) parts.Add("закрыто: " + string.Join(", ", closedNames));
            if (refusedNames.Count > 0) parts.Add("не закрыто, ждёт ответа в своём окне: " + string.Join(", ", refusedNames));
            if (lowered.Count > 0) parts.Add("понижен приоритет у " + lowered.Count + " " + Plural(lowered.Count, "процесса", "процессов", "процессов"));
            if (purges > 0) parts.Add("память очищена " + purges + " " + Plural(purges, "раз", "раза", "раз"));
            int min = (int)(DateTime.Now - since).TotalMinutes;
            return "Идёт " + name + (min > 0 ? " (" + min + " мин)" : "") + (parts.Count > 0 ? " — " + string.Join("; ", parts) + "." : ". Мешающих программ нет.");
        }

        static string Plural(int n, string one, string few, string many)
        {
            int a = n % 100, b = n % 10;
            if (a >= 11 && a <= 14) return many;
            if (b == 1) return one;
            if (b >= 2 && b <= 4) return few;
            return many;
        }

        void CloseApps(Process[] all, GameModeConfig c)
        {
            var groups = all.Where(p => p.Id != gamePid && p.Id != MyPid && SessionOf(p) == MySession)
                            .Select(p => new { P = p, Exe = ExeName(p) })
                            .Where(x => x.Exe != null && !GameRules.Locked.Contains(x.Exe) && !c.Games.Contains(x.Exe) && GameRules.Get(c.Rules, x.Exe) == GameRules.Close)
                            .GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase);
            foreach (var grp in groups)
            {
                var procs = grp.Select(x => x.P).ToList();
                // запустить обратно нужно только главный процесс — тот, что стартовал первым
                Process first = procs.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).First();
                string cmd = ProcInfo.CommandLine(first.Id);
                if (string.IsNullOrEmpty(cmd)) { string path = ProcInfo.ImagePath(first.Id); if (path != null) cmd = "\"" + path + "\""; }
                string label = ProcInfo.Describe(ProcInfo.ImagePath(first.Id), grp.Key);
                // сначала просим закрыться по-хорошему; силой — только то, у чего не осталось видимого окна
                // (лаунчер ушёл в трей). Если программа показывает окно — например, «Сохранить изменения?», — не трогаем.
                bool asked = false;
                foreach (var p in procs) { try { if (p.MainWindowHandle != IntPtr.Zero) { p.CloseMainWindow(); asked = true; } } catch { } }
                var deadline = DateTime.Now.AddSeconds(asked ? 5 : 0);
                var alive = new List<Process>();
                foreach (var p in procs)
                {
                    try
                    {
                        int left = (int)Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds);
                        if (!p.WaitForExit(left)) alive.Add(p);
                    }
                    catch { }
                }
                // решаем за всю программу сразу: убить помощников, пока главное окно спрашивает «Сохранить?», — значит сломать её
                bool refused = alive.Any(p => { try { p.Refresh(); IntPtr w = p.MainWindowHandle; return w != IntPtr.Zero && IsWindowVisible(w); } catch { return false; } });
                if (refused) { refusedNames.Add(label); continue; }
                bool closed = alive.Count < procs.Count;
                foreach (var p in alive) { try { p.Kill(); closed = true; } catch { } }
                if (!closed) continue;
                closedNames.Add(label);
                if (cmd != null && !restart.Contains(cmd)) restart.Add(cmd);
            }
        }

        void LowerAll(Process[] all, GameModeConfig c)
        {
            foreach (var p in all)
            {
                if (p.Id == gamePid || p.Id == MyPid || lowered.ContainsKey(p.Id)) continue;
                string exe = ExeName(p);
                if (exe == null || GameRules.Locked.Contains(exe) || c.Games.Contains(exe) || GameRules.Get(c.Rules, exe) != GameRules.Low) continue;
                if (SessionOf(p) != MySession) continue;
                try
                {
                    var orig = p.PriorityClass;
                    if (orig != ProcessPriorityClass.Normal) continue;   // высокий или фоновый приоритет кто-то выставил сам — не трогаем
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    lowered[p.Id] = Tuple.Create(p.StartTime, orig);
                }
                catch { }
            }
        }

        void MaybePurge()
        {
            if ((DateTime.Now - lastPurge).TotalSeconds < 120) return;
            double free, standby;
            if (!MemPurge.Read(out free, out standby)) return;
            // как ISLC: только когда свободной памяти почти нет, а кэш большой
            if (free >= 1024 || standby < 2048) return;
            lastPurge = DateTime.Now;
            if (MemPurge.Purge()) purges++;
        }

        public static string ExeName(Process p)
        {
            try { return p.ProcessName.ToLowerInvariant() + ".exe"; } catch { return null; }
        }

        static int SessionOf(Process p)
        {
            try { return p.SessionId; } catch { return -1; }
        }
    }

    /// Путь и командная строка чужого процесса без WMI.
    public static class ProcInfo
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
        const int QueryLimited = 0x1000;

        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        /// ProcessCommandLineInformation (Windows 8.1+).
        public static string CommandLine(int pid)
        {
            IntPtr h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                int len;
                NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out len);
                if (len <= 0 || len > 1 << 20) len = 8192;
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (NtQueryInformationProcess(h, 60, buf, len, out len) != 0) return null;
                    int bytes = (ushort)Marshal.ReadInt16(buf);
                    IntPtr s = Marshal.ReadIntPtr(buf, IntPtr.Size);   // UNICODE_STRING: Length, MaximumLength, выравнивание, Buffer
                    return bytes > 0 && s != IntPtr.Zero ? Marshal.PtrToStringUni(s, bytes / 2) : null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        static readonly Dictionary<string, string> descCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// Понятное имя программы («Google Chrome»), а если его нет — имя файла.
        public static string Describe(string path, string exe)
        {
            string key = path ?? exe, d;
            lock (descCache)
                if (descCache.TryGetValue(key, out d)) return d;
            d = null;
            try { if (path != null) d = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
            if (string.IsNullOrWhiteSpace(d) || d.Length > 48) d = Path.GetFileNameWithoutExtension(exe);
            d = d.Trim();
            lock (descCache) descCache[key] = d;
            return d;
        }
    }

    /// Запуск программы обратно без прав администратора: Nolagi работает от администратора,
    /// а лаунчеры и OneDrive должны вернуться обычными, как были. Берём права у рабочего стола (explorer.exe).
    public static class Unelevated
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }

        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, int access, IntPtr attrs, int level, int type, out IntPtr dup);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessWithTokenW(IntPtr tok, int logonFlags, string app, StringBuilder cmd, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        public static void Start(string cmdLine)
        {
            string exe = ExeOf(cmdLine);
            if (exe == null || !File.Exists(exe)) return;
            if (!SafeDir.Elevated) { TryPlain(exe, cmdLine); return; }   // dev-сборка и так без прав администратора
            if (WithShellToken(cmdLine, Path.GetDirectoryName(exe))) return;
            // запасной путь через explorer запускает без прав администратора, но без аргументов (лаунчер может открыться окном) —
            // пишем причину, чтобы такие случаи были видны в панели автора
            Telemetry.Error("gamemode-restart", "fallback to explorer: " + Path.GetFileName(exe) + ", win32 " + lastError);
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false }); } catch { }
        }

        static void TryPlain(string exe, string cmdLine)
        {
            try
            {
                string args = cmdLine.Length > 0 && cmdLine[0] == '"' ? cmdLine.Substring(Math.Min(cmdLine.Length, exe.Length + 2)) : cmdLine.Substring(Math.Min(cmdLine.Length, exe.Length));
                Process.Start(new ProcessStartInfo(exe, args.Trim()) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
            catch { }
        }

        static int lastError;

        static bool WithShellToken(string cmdLine, string dir)
        {
            bool ok = TryShellToken(cmdLine, dir);
            if (!ok) lastError = Marshal.GetLastWin32Error();
            return ok;
        }

        static bool TryShellToken(string cmdLine, string dir)
        {
            int pid;
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out pid) == 0) return false;
            IntPtr h = OpenProcess(0x0400, false, pid), tok = IntPtr.Zero, dup = IntPtr.Zero;   // PROCESS_QUERY_INFORMATION
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(h, 0x0002 | 0x0008 | 0x0001, out tok)) return false;          // DUPLICATE | QUERY | ASSIGN_PRIMARY
                if (!DuplicateTokenEx(tok, 0x02000000, IntPtr.Zero, 2, 1, out dup)) return false;   // MAXIMUM_ALLOWED, Impersonation, Primary
                var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                if (!CreateProcessWithTokenW(dup, 0, null, new StringBuilder(cmdLine, 32768), 0, IntPtr.Zero, dir, ref si, out pi)) return false;
                CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
                return true;
            }
            finally
            {
                if (dup != IntPtr.Zero) CloseHandle(dup);
                if (tok != IntPtr.Zero) CloseHandle(tok);
                CloseHandle(h);
            }
        }

        static string ExeOf(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd[0] == '"') { int e = cmd.IndexOf('"', 1); return e > 1 ? cmd.Substring(1, e - 1) : null; }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? cmd.Substring(0, i + 4) : null;
        }
    }

    /// Очистка «ждущей» памяти (standby list) — то же, что делает ISLC. Нужны права администратора.
    public static class MemPurge
    {
        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValueW(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES n, int len, IntPtr prev, IntPtr ret);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int cls, ref int info, int len);
        const int SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4;

        static double totalGb = -1;
        static bool privOk;

        public static double TotalGb
        {
            get
            {
                if (totalGb < 0)
                {
                    var m = new MEMORYSTATUSEX();
                    totalGb = GlobalMemoryStatusEx(m) ? m.ullTotalPhys / 1073741824.0 : 0;
                }
                return totalGb;
            }
        }

        /// Очистка имеет смысл только на ПК с 16 ГБ и меньше; на 32 ГБ Windows хватает памяти и так.
        public static bool Suits { get { return SafeDir.Elevated && TotalGb > 0 && TotalGb <= 17; } }

        static bool EnsurePrivilege()
        {
            if (privOk) return true;
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out tok)) return false;
            try
            {
                long luid;
                if (!LookupPrivilegeValueW(null, "SeProfileSingleProcessPrivilege", out luid)) return false;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 2 };
                if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                privOk = Marshal.GetLastWin32Error() == 0;
                return privOk;
            }
            finally { CloseHandle(tok); }
        }

        /// Свободная (free + zero) и ждущая память в МБ.
        public static bool Read(out double freeMb, out double standbyMb)
        {
            freeMb = standbyMb = 0;
            if (!EnsurePrivilege()) return false;
            int size = IntPtr.Size * 24, ret;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQuerySystemInformation(SystemMemoryListInformation, buf, size, out ret) != 0) return false;
                Func<int, double> pages = i => (double)Marshal.ReadIntPtr(buf, i * IntPtr.Size).ToInt64();
                double free = pages(0) + pages(1), standby = 0;
                for (int i = 5; i < 13; i++) standby += pages(i);   // PageCountByPriority[8]
                freeMb = free * 4096 / 1048576; standbyMb = standby * 4096 / 1048576;
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static bool Purge()
        {
            if (!EnsurePrivilege()) return false;
            int cmd = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
        }
    }

    /// Автозапуск через планировщик: Nolagi работает от администратора, а обычная автозагрузка (Run) каждый раз спрашивала бы UAC.
    public static class Autostart
    {
        const string TaskName = "Nolagi";

        public static bool Available { get { return SafeDir.Elevated && Install.IsInstalledCopy; } }

        public static bool On { get { return Available && Schtasks("/Query /TN \"" + TaskName + "\"") == 0; } }

        public static bool Set(bool on)
        {
            if (!on) return Schtasks("/Delete /TN \"" + TaskName + "\" /F") == 0 || !On;
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                " <RegistrationInfo><Description>Nolagi: игровой режим в трее</Description></RegistrationInfo>\n" +
                " <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>\n" +
                " <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                " <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\n" +
                " <Actions Context=\"Author\"><Exec><Command>" + Esc(Install.Exe) + "</Command><Arguments>/tray</Arguments></Exec></Actions>\n" +
                "</Task>\n";
            string file = Path.Combine(Path.GetTempPath(), "nolagi-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                return Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F") == 0;
            }
            finally { try { File.Delete(file); } catch { } }
        }

        public static void Remove() { try { Schtasks("/Delete /TN \"" + TaskName + "\" /F"); } catch { } }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }

        static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }
}
