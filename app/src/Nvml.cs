// Nolagi.exe - датчики видеокарты NVIDIA напрямую через nvml.dll (ставится вместе с драйвером NVIDIA).
// Без дочерних процессов и без своих драйверов. Если nvml.dll нет — Monitor.cs берёт данные из nvidia-smi.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pckit
{
    public static class Nvml
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);

        [StructLayout(LayoutKind.Sequential)] struct Util { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong Total, Free, Used; }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlInit_v2();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlShutdown();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetName(IntPtr dev, StringBuilder name, uint len);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Util u);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint pct);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr dev, out uint mw);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out Mem m);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr dev, out ulong r);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr dev, out ulong r);

        static readonly object gate = new object();
        static int state;   // 0 — не пробовали, 1 — работает, -1 — недоступно
        static IntPtr dev;
        static bool oldReasons;

        public static bool Available { get { return Init(); } }

        static bool Init()
        {
            lock (gate)
            {
                if (state != 0) return state > 0;
                state = -1;
                try
                {
                    string sys = Path.Combine(Environment.SystemDirectory, "nvml.dll");
                    string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
                    string path = File.Exists(sys) ? sys : File.Exists(old) ? old : null;
                    if (path == null || LoadLibraryW(path) == IntPtr.Zero) return false;   // дальше DllImport найдёт уже загруженную nvml.dll
                    if (nvmlInit_v2() != 0) return false;
                    if (nvmlDeviceGetHandleByIndex_v2(0, out dev) != 0) return false;
                    state = 1;
                }
                catch { state = -1; }
                return state > 0;
            }
        }

        /// Заполняет поля видеокарты в образце; false — NVML недоступен.
        public static bool Read(MonSample s)
        {
            if (!Init()) return false;
            lock (gate)
            {
                try
                {
                    s.Nvidia = true;
                    var sb = new StringBuilder(96);
                    if (nvmlDeviceGetName(dev, sb, 96) == 0) s.GpuName = sb.ToString();
                    uint v; Util u; Mem m; ulong r;
                    if (nvmlDeviceGetTemperature(dev, 0, out v) == 0) s.GpuTemp = v;
                    if (nvmlDeviceGetUtilizationRates(dev, out u) == 0) s.GpuLoad = u.Gpu;
                    if (nvmlDeviceGetClockInfo(dev, 0, out v) == 0) s.GpuClock = v;
                    if (nvmlDeviceGetClockInfo(dev, 2, out v) == 0) s.GpuMemClock = v;
                    try { if (nvmlDeviceGetFanSpeed(dev, out v) == 0) s.GpuFan = v; } catch { }
                    if (nvmlDeviceGetPowerUsage(dev, out v) == 0) s.GpuPower = Math.Round(v / 1000.0);
                    if (nvmlDeviceGetEnforcedPowerLimit(dev, out v) == 0) s.GpuPowerLimit = Math.Round(v / 1000.0);
                    if (nvmlDeviceGetMemoryInfo(dev, out m) == 0) { s.GpuMemUsed = m.Used / 1048576.0; s.GpuMemTotal = m.Total / 1048576.0; }
                    int rc = -1;
                    if (!oldReasons)
                    {
                        try { rc = nvmlDeviceGetCurrentClocksEventReasons(dev, out r); if (rc == 0) s.GpuReasons = (long)r; }
                        catch (EntryPointNotFoundException) { oldReasons = true; }
                    }
                    if (oldReasons) { try { if (nvmlDeviceGetCurrentClocksThrottleReasons(dev, out r) == 0) s.GpuReasons = (long)r; } catch { } }
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
