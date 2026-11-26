// Nolagi.exe - live monitoring: GPU through nvidia-smi (streaming, no driver of our own),
// CPU/RAM/processes through Windows performance counters. C# 5 / .NET Framework 4.8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Pckit
{
    public class MonSample
    {
        public double CpuTotal = -1, CpuMhz = -1;
        public double[] Cores = new double[0];
        public double RamUsedGb, RamTotalGb;
        public bool Nvidia;
        public string GpuName = "";
        public double GpuTemp = -1, GpuLoad = -1, GpuClock = -1, GpuMemClock = -1, GpuFan = -1, GpuPower = -1, GpuPowerLimit = -1, GpuMemUsed = -1, GpuMemTotal = -1;
        public long GpuReasons = -1;
        public List<Tuple<string, double, double>> Top = new List<Tuple<string, double, double>>();   // name, cpu %, ram MB
    }

    public class LiveMonitor : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
            public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        // Job-объект «убить вместе с нами»: если Nolagi упадёт или его закроют через диспетчер задач, Windows сама завершит nvidia-smi
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        static IntPtr killJob = IntPtr.Zero;

        static void TieToUs(Process p)
        {
            try
            {
                if (killJob == IntPtr.Zero)
                {
                    var j = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    if (j != IntPtr.Zero && SetInformationJobObject(j, 9, ref info, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))) killJob = j;
                }
                if (killJob != IntPtr.Zero) AssignProcessToJobObject(killJob, p.Handle);
            }
            catch { }
        }

        readonly object gate = new object();
        Process smi;
        bool smiRetried, disposed;
        string smiFields;
        readonly MonSample gpu = new MonSample();

        PerformanceCounter cpuTotal, cpuPerf, cpuFreq;
        List<PerformanceCounter> cores = new List<PerformanceCounter>();
        List<PerformanceCounter> gpuEngines = new List<PerformanceCounter>();
        bool countersReady;
        Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
        DateTime lastProcAt = DateTime.MinValue;
        List<Tuple<string, double, double>> lastTop = new List<Tuple<string, double, double>>();

        public static string SmiPath { get { return Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"); } }

        public void Start()
        {
            // сначала nvml.dll (без дочернего процесса), nvidia-smi — запасной путь
            if (!Nvml.Available && File.Exists(SmiPath)) StartSmi("clocks_event_reasons.active");
        }

        void StartSmi(string reasonField)
        {
            smiFields = "name,temperature.gpu,utilization.gpu,clocks.gr,clocks.mem,fan.speed,power.draw,power.limit,memory.used,memory.total," + reasonField;
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(SmiPath, "--query-gpu=" + smiFields + " --format=csv,noheader,nounits -lms 1000")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true },
                    EnableRaisingEvents = true
                };
                p.OutputDataReceived += (s, e) => OnSmiLine(e.Data);
                p.ErrorDataReceived += (s, e) => { };
                p.Start();
                TieToUs(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                smi = p;
            }
            catch { smi = null; }
        }

        void OnSmiLine(string line)
        {
            if (string.IsNullOrEmpty(line) || disposed) return;
            // older drivers call the field clocks_throttle_reasons: restart once with that name
            if (line.IndexOf("not a valid field", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!smiRetried) { smiRetried = true; StopSmi(); StartSmi("clocks_throttle_reasons.active"); }
                return;
            }
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 11) return;
            Func<string, double> num = s =>
            {
                double v;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : -1;
            };
            long reasons = -1;
            string r = f[10];
            if (r.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                long rv;
                if (long.TryParse(r.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rv)) reasons = rv;
            }
            lock (gate)
            {
                gpu.Nvidia = true;
                gpu.GpuName = f[0];
                gpu.GpuTemp = num(f[1]); gpu.GpuLoad = num(f[2]); gpu.GpuClock = num(f[3]); gpu.GpuMemClock = num(f[4]);
                gpu.GpuFan = num(f[5]); gpu.GpuPower = num(f[6]); gpu.GpuPowerLimit = num(f[7]);
                gpu.GpuMemUsed = num(f[8]); gpu.GpuMemTotal = num(f[9]); gpu.GpuReasons = reasons;
            }
        }

        void StopSmi()
        {
            var p = smi; smi = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }

        void InitCounters()
        {
            if (countersReady) return;
            countersReady = true;
            try
            {
                cpuTotal = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                var cat = new PerformanceCounterCategory("Processor Information");
                foreach (var inst in cat.GetInstanceNames().Where(n => !n.Contains("_Total")).OrderBy(n => n, StringComparer.Ordinal)
                    .OrderBy(n => { int a, b; var p = n.Split(','); return (p.Length == 2 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b)) ? a * 1000 + b : 0; }))
                    cores.Add(new PerformanceCounter("Processor Information", "% Processor Utility", inst, true));
                cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                cpuFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
            }
            catch
            {
                // very old systems: plain «% Processor Time»
                try { cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); } catch { cpuTotal = null; }
            }
            try { cpuTotal.NextValue(); foreach (var c in cores) c.NextValue(); if (cpuPerf != null) cpuPerf.NextValue(); } catch { }
            if (!File.Exists(SmiPath))
            {
                // AMD / Intel: 3D engine load from Windows (no temperatures)
                try
                {
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    foreach (var inst in cat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)))
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        c.NextValue(); gpuEngines.Add(c);
                    }
                }
                catch { }
            }
        }

        /// One sample; call from a background thread about once a second.
        public MonSample Sample()
        {
            InitCounters();
            var s = new MonSample();
            Func<PerformanceCounter, double> val = c => { try { return c == null ? -1 : c.NextValue(); } catch { return -1; } };
            s.CpuTotal = Math.Min(100, val(cpuTotal));
            s.Cores = cores.Select(c => Math.Min(100, Math.Max(0, val(c)))).ToArray();
            double perf = val(cpuPerf), freq = val(cpuFreq);
            if (perf > 0 && freq > 0) s.CpuMhz = Math.Round(freq * perf / 100);
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m))
            {
                s.RamTotalGb = m.ullTotalPhys / 1073741824.0;
                s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            }
            if (!Nvml.Read(s)) lock (gate)
            {
                s.Nvidia = gpu.Nvidia; s.GpuName = gpu.GpuName;
                s.GpuTemp = gpu.GpuTemp; s.GpuLoad = gpu.GpuLoad; s.GpuClock = gpu.GpuClock; s.GpuMemClock = gpu.GpuMemClock;
                s.GpuFan = gpu.GpuFan; s.GpuPower = gpu.GpuPower; s.GpuPowerLimit = gpu.GpuPowerLimit;
                s.GpuMemUsed = gpu.GpuMemUsed; s.GpuMemTotal = gpu.GpuMemTotal; s.GpuReasons = gpu.GpuReasons;
            }
            if (!s.Nvidia && gpuEngines.Count > 0) s.GpuLoad = Math.Min(100, gpuEngines.Sum(c => Math.Max(0, val(c))));
            s.Top = TopProcesses();
            return s;
        }

        List<Tuple<string, double, double>> TopProcesses()
        {
            var now = DateTime.UtcNow;
            if ((now - lastProcAt).TotalSeconds < 1.8) return lastTop;
            double secs = lastProcAt == DateTime.MinValue ? 0 : (now - lastProcAt).TotalSeconds;
            lastProcAt = now;
            var cur = new Dictionary<int, TimeSpan>();
            var byName = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);   // cpu %, ram MB
            int n = Environment.ProcessorCount;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0 || p.Id == 4) continue;
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        cur[p.Id] = t;
                        TimeSpan was;
                        if (secs > 0 && lastCpu.TryGetValue(p.Id, out was)) cpu = Math.Max(0, (t - was).TotalSeconds / secs / n * 100);
                    }
                    catch { }
                    double[] a;
                    if (!byName.TryGetValue(p.ProcessName, out a)) { a = new double[2]; byName[p.ProcessName] = a; }
                    a[0] += cpu; a[1] += p.WorkingSet64 / 1048576.0;
                }
                catch { }
                finally { p.Dispose(); }
            }
            lastCpu = cur;
            lastTop = byName.Where(kv => kv.Key != "Idle" && kv.Key != "Memory Compression")
                .OrderByDescending(kv => secs > 0 ? kv.Value[0] : kv.Value[1]).ThenByDescending(kv => kv.Value[1])
                .Take(6).Select(kv => Tuple.Create(kv.Key, Math.Round(kv.Value[0], 1), Math.Round(kv.Value[1]))).ToList();
            return lastTop;
        }

        /// Human reason why the GPU runs below its maximum clocks (nvidia-smi clocks event reasons bitmask).
        public static string GpuReasonText(long r, double load)
        {
            if (r < 0) return "";
            if ((r & 0x40) != 0 || (r & 0x20) != 0) return "Видеокарта снижает частоты из-за перегрева: почистите её от пыли и проверьте вентиляторы корпуса.";
            if ((r & 0x08) != 0 || (r & 0x80) != 0) return "Аварийное снижение частот: перегрев или блоку питания не хватает мощности.";
            if ((r & 0x04) != 0) return load >= 90 ? "Упирается в лимит питания — под полной нагрузкой это нормально." : "Ограничена лимитом питания.";
            if ((r & 0x01) != 0) return "Простаивает — частоты снижены, это нормально.";
            return "Работает без ограничений частоты.";
        }

        public void Dispose()
        {
            disposed = true;
            StopSmi();
            foreach (var c in cores.Concat(gpuEngines).Concat(new[] { cpuTotal, cpuPerf, cpuFreq }))
                try { if (c != null) c.Dispose(); } catch { }
        }
    }
}
