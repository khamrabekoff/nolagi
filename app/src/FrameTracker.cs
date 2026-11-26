// Nolagi.exe - FPS любой игры без инъекций: события Present из ETW (DXGI, D3D9, DxgKrnl), как у PresentMon.
// Работает с правами администратора (Nolagi и так их требует). Античиты к этому не придираются: в игру ничего не внедряется.
// Сессия ETW и разбор событий адаптированы из ImplexityTweaker (MIT, (c) 2026 0kar1d3) — см. THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pckit
{
    public class LiveFps
    {
        public double Fps, FrameMs, Low1;
        public string Game = "";
    }

    public class FpsRun
    {
        public string Game = "";
        public DateTime At;
        public double Seconds, Avg, Low1, Low01, MaxFrameMs;
        public int Frames;
        public double[] PerSecond = new double[0];   // средний FPS по секундам (для графика)
    }

    public sealed class FrameTracker : IDisposable
    {
        // у каждой сессии своё имя «NolagiFpsTrace_<pid>_<n>»: закрытие старой не задевает новую,
        // а вторая копия Nolagi не выключает замер первой
        const string SessionPrefix = "NolagiFpsTrace_";
        static int sessionCounter;
        readonly string SessionName = SessionPrefix + Process.GetCurrentProcess().Id + "_" + Interlocked.Increment(ref sessionCounter);
        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3d9Provider = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        static readonly Guid DxgKrnlProvider = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");

        const uint EVENT_TRACE_REAL_TIME_MODE = 0x100, EVENT_TRACE_CONTROL_STOP = 1, EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000, WNODE_FLAG_TRACED_GUID = 0x20000;
        const ulong INVALID_TRACE_HANDLE = ulong.MaxValue;

        // ---------------------------------------------------------------- Win32
        [StructLayout(LayoutKind.Sequential)]
        struct WNODE_HEADER { public uint BufferSize, ProviderId; public ulong HistoricalContext; public long TimeStamp; public Guid Guid; public uint ClientContext, Flags; }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_DESCRIPTOR { public ushort Id; public byte Version, Channel, Level, Opcode; public ushort Task; public ulong Keyword; }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_HEADER
        {
            public ushort Size, HeaderType, Flags, EventProperty;
            public uint ThreadId, ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public EVENT_DESCRIPTOR EventDescriptor;
            public ulong ProcessorTime;
            public Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;
            public byte ProcessorNumber, Alignment; public ushort LoggerId;
            public ushort ExtendedDataCount, UserDataLength;
            public IntPtr ExtendedData, UserData, UserContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE_HEADER { public ushort Size, FieldTypeFlags; public uint Version, ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public ulong ProcessorTime; }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE { public EVENT_TRACE_HEADER Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TIME_ZONE_INFORMATION
        {
            public int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
            public SYSTEMTIME StandardDate; public int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
            public SYSTEMTIME DaylightDate; public int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName, LogFileName;
            public TIME_ZONE_INFORMATION TimeZone;
            public long BootTime, PerfFreq, StartTime;
            public uint ReservedFlags, BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct EVENT_TRACE_LOGFILEW
        {
            public IntPtr LogFileName, LoggerName;
            public long CurrentTime;
            public uint BuffersRead, ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize, Filled, EventsLost;
            public IntPtr EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void EventRecordCallback(ref EVENT_RECORD r);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong session, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong session, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong h, ref Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr param);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        // ---------------------------------------------------------------- потоки кадров
        struct SKey : IEquatable<SKey>
        {
            public uint Pid; public byte Kind; public ulong Id;   // Kind: 1 DXGI, 2 D3D9, 3 DxgKrnl (запасной: OpenGL/Vulkan)
            public bool Equals(SKey o) { return Pid == o.Pid && Kind == o.Kind && Id == o.Id; }
            public override bool Equals(object o) { return o is SKey && Equals((SKey)o); }
            public override int GetHashCode() { return (int)Pid * 397 ^ Kind * 31 ^ Id.GetHashCode(); }
        }

        class Ring
        {
            public readonly long[] T = new long[1024];
            public int Count, Head;
            public long Last;
            public void Push(long t) { T[Head] = t; Head = (Head + 1) % T.Length; if (Count < T.Length) Count++; if (t > Last) Last = t; }
        }

        const int RecordCap = 3000000;   // ~2.5 часа при 300 fps

        readonly object sync = new object();
        readonly Dictionary<SKey, Ring> live = new Dictionary<SKey, Ring>();
        readonly Dictionary<SKey, List<long>> rec = new Dictionary<SKey, List<long>>();
        readonly Dictionary<uint, string> recNames = new Dictionary<uint, string>();
        readonly Dictionary<uint, long> lastPrimary = new Dictionary<uint, long>();
        volatile uint targetPid;
        string targetName = "";
        readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;
        volatile bool running, recording;
        DateTime recStarted;

        ulong session, trace = INVALID_TRACE_HANDLE;
        Thread thread;
        EventRecordCallback keep;
        IntPtr logBuf;

        public bool Running { get { return running; } }
        public bool Recording { get { return recording; } }
        public TimeSpan RecordedFor { get { return recording ? DateTime.Now - recStarted : TimeSpan.Zero; } }
        public string LastError = "";
        /// Окно игры, за которой следим (для оверлея: на каком мониторе её показывать).
        public IntPtr TargetWindow = IntPtr.Zero;

        public bool Start()
        {
            if (running) return true;
            try
            {
                StopStaleSessions();
                int size = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES)) + (SessionName.Length + 2) * 2;
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.Copy(new byte[size], 0, p, size);
                    var props = new EVENT_TRACE_PROPERTIES
                    {
                        Wnode = new WNODE_HEADER { BufferSize = (uint)size, Guid = Guid.NewGuid(), ClientContext = 1, Flags = WNODE_FLAG_TRACED_GUID },
                        BufferSize = 64, MinimumBuffers = 4, MaximumBuffers = 32, LogFileMode = EVENT_TRACE_REAL_TIME_MODE, FlushTimer = 1,
                        LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES))
                    };
                    Marshal.StructureToPtr(props, p, false);
                    uint st = StartTraceW(out session, SessionName, p);
                    if (st != 0) { LastError = "ETW " + st + (st == 5 ? " (нужны права администратора)" : ""); return false; }
                }
                finally { Marshal.FreeHGlobal(p); }

                Guid g1 = DxgiProvider, g2 = D3d9Provider, g3 = DxgKrnlProvider;
                EnableTraceEx2(session, ref g1, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 5, 0x8000000000000002UL, 0, 0, IntPtr.Zero);
                EnableTraceEx2(session, ref g2, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
                EnableTraceEx2(session, ref g3, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 4, 0x8000000UL, 0, 0, IntPtr.Zero);

                keep = OnEvent;
                var lf = new EVENT_TRACE_LOGFILEW
                {
                    LoggerName = Marshal.StringToHGlobalUni(SessionName),
                    ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(keep)
                };
                logBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EVENT_TRACE_LOGFILEW)));
                Marshal.StructureToPtr(lf, logBuf, false);
                trace = OpenTraceW(logBuf);
                Marshal.FreeHGlobal(lf.LoggerName);
                if (trace == INVALID_TRACE_HANDLE || trace == 0) { LastError = "ETW: не открылась сессия"; Dispose(); return false; }

                running = true;
                ulong h = trace;
                thread = new Thread(() => { try { ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
                { IsBackground = true, Name = "NolagiEtw", Priority = ThreadPriority.BelowNormal };
                thread.Start();
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; Dispose(); return false; }
        }

        void OnEvent(ref EVENT_RECORD r)
        {
            if (!running) return;
            uint pid = r.EventHeader.ProcessId;
            if (pid == 0 || pid == selfPid) return;
            bool isLive = pid == targetPid;
            bool isRec = false;
            if (recording) lock (sync) isRec = recNames.ContainsKey(pid);
            if (!isLive && !isRec) return;

            Guid prov = r.EventHeader.ProviderId;
            ushort id = r.EventHeader.EventDescriptor.Id;
            long tick = r.EventHeader.TimeStamp;
            if (tick <= 0) tick = Stopwatch.GetTimestamp();
            bool is32 = (r.EventHeader.Flags & 0x20) != 0;
            int ptr = is32 ? 4 : 8;
            byte kind;
            ulong chain = 0;

            if (prov == DxgiProvider && id == 42)
            {
                kind = 1;
                if (r.UserData != IntPtr.Zero && r.UserDataLength >= ptr)
                {
                    chain = is32 ? (uint)Marshal.ReadInt32(r.UserData) : (ulong)Marshal.ReadInt64(r.UserData);
                    if (r.UserDataLength >= ptr + 4 && ((uint)Marshal.ReadInt32(r.UserData, ptr) & 1) != 0) return;   // DXGI_PRESENT_TEST — не кадр
                }
            }
            else if (prov == D3d9Provider && id == 1)
            {
                kind = 2;
                if (r.UserData != IntPtr.Zero && r.UserDataLength >= ptr)
                    chain = is32 ? (uint)Marshal.ReadInt32(r.UserData) : (ulong)Marshal.ReadInt64(r.UserData);
            }
            else if (prov == DxgKrnlProvider && id == 184) kind = 3;
            else return;

            var key = new SKey { Pid = pid, Kind = kind, Id = chain != 0 ? chain : r.EventHeader.ThreadId };
            lock (sync)
            {
                if (kind != 3) lastPrimary[pid] = tick;
                else
                {
                    long lp;
                    // DxgKrnl — только если у процесса нет событий DXGI/D3D9 (OpenGL, часть Vulkan)
                    if (lastPrimary.TryGetValue(pid, out lp) && tick - lp <= Stopwatch.Frequency * 2) return;
                }
                if (isLive)
                {
                    Ring ring;
                    if (!live.TryGetValue(key, out ring))
                    {
                        if (live.Count >= 16) return;
                        ring = new Ring(); live[key] = ring;
                    }
                    ring.Push(tick);
                }
                if (isRec)
                {
                    List<long> l;
                    if (!rec.TryGetValue(key, out l)) { if (rec.Count >= 64) return; l = new List<long>(4096); rec[key] = l; }
                    if (l.Count < RecordCap) l.Add(tick);
                }
            }
        }

        /// Следить за активным окном; вызывать из UI-таймера (раз в 0,5–1 с).
        public void FollowForeground()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0 || pid == selfPid) return;
                string name;
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { return; }
                if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)) return;
                TargetWindow = hwnd;
                lock (sync)
                {
                    if (recording && !recNames.ContainsKey(pid)) recNames[pid] = name;
                    if (pid == targetPid) return;
                    targetPid = pid; targetName = name;
                    live.Clear();
                }
            }
            catch { }
        }

        public LiveFps Live()
        {
            var res = new LiveFps();
            long now = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
            long[] t = null;
            lock (sync)
            {
                res.Game = targetName;
                Ring best = null; int bestScore = -1;
                bool primary = live.Any(kv => kv.Key.Kind != 3 && kv.Value.Count >= 2 && now - kv.Value.Last <= 2 * f);
                foreach (var kv in live)
                {
                    var r = kv.Value;
                    if (r.Count < 2 || now - r.Last > 2 * f || (primary && kv.Key.Kind == 3)) continue;
                    int recent = 0;
                    for (int i = 0; i < r.Count; i++) if (r.T[i] >= r.Last - f) recent++;
                    if (recent > bestScore) { bestScore = recent; best = r; }
                }
                if (best != null)
                {
                    t = new long[best.Count];
                    int start = best.Count == best.T.Length ? best.Head : 0;
                    for (int i = 0; i < best.Count; i++) t[i] = best.T[(start + i) % best.T.Length];
                }
            }
            if (t == null || t.Length < 2) return res;
            Array.Sort(t);
            // последние 2 секунды
            int from = 0;
            while (from < t.Length - 2 && t[from] < t[t.Length - 1] - 2 * f) from++;
            var seg = new long[t.Length - from];
            Array.Copy(t, from, seg, 0, seg.Length);
            var s = Stats(seg);
            if (s == null) return res;
            res.Fps = Math.Round(s.Avg); res.Low1 = Math.Round(s.Low1);
            res.FrameMs = s.Avg > 0 ? Math.Round(1000 / s.Avg, 1) : 0;
            return res;
        }

        // ---------------------------------------------------------------- запись замера
        public void BeginRecording()
        {
            lock (sync) { rec.Clear(); recNames.Clear(); }
            recStarted = DateTime.Now;
            recording = true;
            FollowForeground();
        }

        /// Остановить запись; результат — по процессу и цепочке с наибольшим числом кадров (это и есть игра).
        public FpsRun EndRecording()
        {
            if (!recording) return null;
            recording = false;
            List<long> best = null; uint bestPid = 0;
            lock (sync)
            {
                var pidsWithPrimary = new HashSet<uint>(rec.Where(kv => kv.Key.Kind != 3 && kv.Value.Count > 100).Select(kv => kv.Key.Pid));
                foreach (var kv in rec)
                {
                    if (kv.Key.Kind == 3 && pidsWithPrimary.Contains(kv.Key.Pid)) continue;
                    if (best == null || kv.Value.Count > best.Count) { best = kv.Value; bestPid = kv.Key.Pid; }
                }
                if (best != null) best = new List<long>(best);
                string n;
                var run0 = new FpsRun { Game = bestPid != 0 && recNames.TryGetValue(bestPid, out n) ? n : "" };
                rec.Clear(); recNames.Clear();
                if (best == null || best.Count < 30) return run0;
                var arr = best.ToArray();
                Array.Sort(arr);
                var s = Stats(arr);
                if (s == null) return run0;
                s.Game = run0.Game;
                return s;
            }
        }

        /// Средний FPS, 1% и 0.1% low (99-й и 99,9-й процентиль времени кадра, как в PresentMon/CapFrameX), график по секундам.
        static FpsRun Stats(long[] t)
        {
            double f = Stopwatch.Frequency, toMs = 1000.0 / f;
            var ticks = new List<long>(t.Length) { t[0] };
            for (int i = 1; i < t.Length; i++) if ((t[i] - ticks[ticks.Count - 1]) * toMs >= 0.2) ticks.Add(t[i]);   // дубли одного кадра
            if (ticks.Count < 2) return null;
            int n = ticks.Count - 1;
            var ms = new double[n];
            for (int i = 0; i < n; i++) ms[i] = (ticks[i + 1] - ticks[i]) * toMs;
            double span = (ticks[n] - ticks[0]) * toMs;
            var run = new FpsRun { At = DateTime.Now, Frames = n, Seconds = Math.Round(span / 1000, 1), Avg = span > 0 ? n * 1000 / span : 0 };

            int secs = (int)Math.Floor(span / 1000);   // только полные секунды: неполная последняя дала бы ложный провал на графике
            if (secs >= 1)
            {
                var per = new double[secs];
                var cnt = new int[secs];
                for (int i = 1; i <= n; i++) { int b = (int)((ticks[i] - ticks[0]) * toMs / 1000); if (b < secs) cnt[b]++; }
                for (int i = 0; i < secs; i++) per[i] = cnt[i];
                run.PerSecond = per;
            }
            var sorted = (double[])ms.Clone();
            Array.Sort(sorted);
            Func<double, double> pct = q => sorted[Math.Max(0, Math.Min(n - 1, (int)Math.Ceiling(n * q) - 1))];
            double p99 = pct(0.99), p999 = pct(0.999);
            run.Low1 = p99 > 0 ? Math.Min(run.Avg, 1000 / p99) : run.Avg;
            run.Low01 = p999 > 0 ? Math.Min(run.Avg, 1000 / p999) : run.Avg;
            run.MaxFrameMs = sorted[n - 1];
            return run;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint QueryAllTracesW(IntPtr[] props, uint count, out uint found);

        /// Сессии Nolagi, оставшиеся после падения программы (их процесса уже нет), — остановить.
        static void StopStaleSessions()
        {
            const int max = 64, nameBytes = 1024 * 2;
            int head = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES)), size = head + nameBytes * 2;
            var bufs = new IntPtr[max];
            try
            {
                for (int i = 0; i < max; i++)
                {
                    bufs[i] = Marshal.AllocHGlobal(size);
                    Marshal.Copy(new byte[size], 0, bufs[i], size);
                    var props = new EVENT_TRACE_PROPERTIES
                    {
                        Wnode = new WNODE_HEADER { BufferSize = (uint)size },
                        LoggerNameOffset = (uint)head, LogFileNameOffset = (uint)(head + nameBytes)
                    };
                    Marshal.StructureToPtr(props, bufs[i], false);
                }
                uint found;
                if (QueryAllTracesW(bufs, max, out found) != 0) return;
                for (int i = 0; i < found && i < max; i++)
                {
                    string name = Marshal.PtrToStringUni(bufs[i] + head);
                    if (name == null || !name.StartsWith(SessionPrefix, StringComparison.Ordinal)) continue;
                    var parts = name.Substring(SessionPrefix.Length).Split('_');
                    int pid;
                    if (parts.Length != 2 || !int.TryParse(parts[0], out pid)) continue;
                    bool alive = false;
                    try { using (var p = Process.GetProcessById(pid)) alive = !p.HasExited && p.ProcessName.IndexOf("nolagi", StringComparison.OrdinalIgnoreCase) >= 0; } catch { }
                    if (!alive) StopSession(name);
                }
            }
            catch { }
            finally { foreach (var b in bufs) if (b != IntPtr.Zero) Marshal.FreeHGlobal(b); }
        }

        static void StopSession(string sessionName)
        {
            int size = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES)) + (sessionName.Length + 64) * 2;
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, p, size);
                var props = new EVENT_TRACE_PROPERTIES
                {
                    Wnode = new WNODE_HEADER { BufferSize = (uint)size, Flags = WNODE_FLAG_TRACED_GUID },
                    LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES))
                };
                Marshal.StructureToPtr(props, p, false);
                ControlTraceW(0, sessionName, p, EVENT_TRACE_CONTROL_STOP);
            }
            catch { }
            finally { Marshal.FreeHGlobal(p); }
        }

        public void Dispose()
        {
            running = false; recording = false;
            try { if (trace != INVALID_TRACE_HANDLE && trace != 0) { CloseTrace(trace); trace = INVALID_TRACE_HANDLE; } } catch { }
            StopSession(SessionName);
            session = 0;
            try { if (thread != null) thread.Join(500); } catch { }
            thread = null;
            if (logBuf != IntPtr.Zero) { Marshal.FreeHGlobal(logBuf); logBuf = IntPtr.Zero; }
        }
    }
}
