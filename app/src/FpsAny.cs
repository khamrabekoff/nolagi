// Nolagi.exe - FPS в любой игре: оверлей поверх игры и запись замера (средний FPS, 1% и 0.1% low).
// Горячие клавиши: Ctrl+F12 — оверлей, Ctrl+F11 — начать/остановить запись.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Pckit
{
    /// Маленькое окно поверх игры: прозрачное для мыши, не забирает фокус, не видно в панели задач.
    public class OverlayWindow : Window
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        readonly TextBlock fps, sub, hw, rec;
        int corner;

        public OverlayWindow(int corner)
        {
            this.corner = corner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ShowActivated = false; Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
            var sp = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            fps = new TextBlock { Text = "—", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            top.Children.Add(fps);
            top.Children.Add(new TextBlock { Text = " FPS", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xCC)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(top);
            var mut = new SolidColorBrush(Color.FromRgb(0xD5, 0xDD, 0xE5));
            sub = new TextBlock { FontSize = 12, Foreground = mut };
            hw = new TextBlock { FontSize = 12, Foreground = mut };
            rec = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), Visibility = Visibility.Collapsed };
            sp.Children.Add(sub); sp.Children.Add(hw); sp.Children.Add(rec);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x14, 0x1A)), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 14, 8), Child = sp, IsHitTestVisible = false
            };
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED: клики проходят в игру
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80 | 0x08000000 | 0x80000);
            };
            SizeChanged += (s, e) => Place();
        }

        public void SetCorner(int c) { corner = c; Place(); }

        void Place()
        {
            double w = SystemParameters.PrimaryScreenWidth, h = SystemParameters.PrimaryScreenHeight, m = 12;
            Left = (corner == 1 || corner == 3) ? w - ActualWidth - m : m;
            Top = corner >= 2 ? h - ActualHeight - m - 40 : m;
        }

        public void Update(LiveFps f, MonSample g, double cpu, bool recording, TimeSpan recFor)
        {
            fps.Text = f.Fps > 0 ? f.Fps.ToString("0", CultureInfo.InvariantCulture) : "—";
            double ratio = f.Fps > 0 ? f.Low1 / f.Fps : 1;
            fps.Foreground = f.Fps <= 0 ? Brushes.White : ratio < 0.4 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) : ratio < 0.6 ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66)) : new SolidColorBrush(Color.FromRgb(0x7C, 0xF0, 0x9A));
            sub.Text = f.Fps > 0 ? "1% low " + f.Low1.ToString("0", CultureInfo.InvariantCulture) + " · " + f.FrameMs.ToString("0.0", CultureInfo.InvariantCulture) + " мс"
                                 : (f.Game.Length > 0 ? f.Game + ": нет кадров" : "ожидание игры");
            var parts = new List<string>();
            if (g != null && g.Nvidia)
            {
                string gp = "GPU";
                if (g.GpuTemp >= 0) gp += " " + g.GpuTemp.ToString("0") + "°C";
                if (g.GpuLoad >= 0) gp += " " + g.GpuLoad.ToString("0") + "%";
                parts.Add(gp);
            }
            if (cpu >= 0) parts.Add("CPU " + cpu.ToString("0") + "%");
            hw.Text = string.Join("   ", parts);
            hw.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            rec.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            if (recording) rec.Text = "● Запись " + ((int)recFor.TotalMinutes).ToString("00") + ":" + recFor.Seconds.ToString("00") + "   Ctrl+F11 — стоп";
            var hnd = new WindowInteropHelper(this).Handle;
            if (hnd != IntPtr.Zero) SetWindowPos(hnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE|NOMOVE|NOACTIVATE: остаться поверх игры
        }

        public void Flash(string text)
        {
            fps.Text = "✓"; fps.Foreground = Brushes.White;
            sub.Text = text; hw.Visibility = Visibility.Collapsed; rec.Visibility = Visibility.Collapsed;
        }
    }

    public partial class MainWindow
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        const int HkOverlay = 0x4E01, HkRecord = 0x4E02;
        FrameTracker tracker;
        OverlayWindow fpsOverlay;
        DispatcherTimer anyTimer;
        FpsRun anyLast, anyPrev;
        string anyError;
        TextBlock anyLiveText;
        Button anyRecBtn;
        DateTime flashUntil = DateTime.MinValue;
        long cpuIdle, cpuTotal;
        int gpuTick;
        MonSample gpuLast;

        static string RunsPath { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nolagi", "fps-runs.json"); } }

        /// Вызывается после построения окна: горячие клавиши и оверлей, если он был включён.
        void InitFpsAny()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                RegisterHotKey(h, HkOverlay, 0x0002 | 0x4000, 0x7B);   // Ctrl+F12, без автоповтора
                RegisterHotKey(h, HkRecord, 0x0002 | 0x4000, 0x7A);    // Ctrl+F11
                HwndSource.FromHwnd(h).AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x0312)   // WM_HOTKEY
                    {
                        if (wp.ToInt32() == HkOverlay) { SetOverlay(!cfg.OverlayOn); handled = true; }
                        else if (wp.ToInt32() == HkRecord) { ToggleRecording(); handled = true; }
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
            if (cfg.OverlayOn) SetOverlay(true);
        }

        bool EnsureTracker()
        {
            if (tracker != null && tracker.Running) return true;
            tracker = new FrameTracker();
            if (!tracker.Start())
            {
                anyError = "Не удалось начать замер FPS: " + tracker.LastError;
                tracker = null;
                return false;
            }
            if (anyTimer == null)
            {
                anyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                anyTimer.Tick += (s, e) => AnyTick();
            }
            anyTimer.Start();
            return true;
        }

        void ReleaseTrackerIfIdle()
        {
            if (tracker == null || cfg.OverlayOn || tracker.Recording) return;
            if (anyTimer != null) anyTimer.Stop();
            var t = tracker; tracker = null;
            System.Threading.Tasks.Task.Run(() => t.Dispose());
        }

        void SetOverlay(bool on)
        {
            cfg.OverlayOn = on; cfg.Save();
            if (on)
            {
                if (!EnsureTracker()) { cfg.OverlayOn = false; cfg.Save(); if (FpsPageOpen) RenderFpsIdle(); return; }
                if (fpsOverlay == null) { fpsOverlay = new OverlayWindow(cfg.OverlayCorner); fpsOverlay.Show(); }
            }
            else
            {
                if (fpsOverlay != null) { fpsOverlay.Close(); fpsOverlay = null; }
                ReleaseTrackerIfIdle();
            }
            if (FpsPageOpen) RenderFpsIdle();
        }

        void ToggleRecording()
        {
            if (tracker != null && tracker.Recording)
            {
                var run = tracker.EndRecording();
                ReleaseTrackerIfIdle();
                if (run == null || run.Frames < 30)
                {
                    anyError = "Кадров почти нет. Запись идёт по игре в активном окне: переключитесь в игру после старта записи. Игры в эксклюзивном полноэкранном режиме тоже считаются, а вот видеоплееры и браузер — нет.";
                    if (fpsOverlay != null) { fpsOverlay.Flash("кадров нет — замер не сохранён"); flashUntil = DateTime.Now.AddSeconds(4); }
                }
                else
                {
                    anyPrev = LoadRuns().FirstOrDefault(r => r.Game.Equals(run.Game, StringComparison.OrdinalIgnoreCase));
                    anyLast = run;
                    SaveRun(run);
                    if (fpsOverlay != null) { fpsOverlay.Flash("Замер сохранён: " + Math.Round(run.Avg) + " FPS, 1% low " + Math.Round(run.Low1)); flashUntil = DateTime.Now.AddSeconds(5); }
                }
            }
            else
            {
                if (!EnsureTracker()) { if (FpsPageOpen) RenderFpsIdle(); return; }
                tracker.BeginRecording();
            }
            if (FpsPageOpen) RenderFpsIdle();
        }

        double CpuLoad()
        {
            long idle, kern, user;
            if (!GetSystemTimes(out idle, out kern, out user)) return -1;
            long total = kern + user;   // kernel включает idle
            double res = -1;
            if (cpuTotal > 0 && total > cpuTotal) res = Math.Max(0, Math.Min(100, 100.0 * (1 - (double)(idle - cpuIdle) / (total - cpuTotal))));
            cpuIdle = idle; cpuTotal = total;
            return res;
        }

        double cpuLast = -1;

        void AnyTick()
        {
            if (tracker == null) return;
            tracker.FollowForeground();
            var live = tracker.Live();
            if (++gpuTick % 2 == 0 || gpuLast == null)
            {
                var g = new MonSample();
                gpuLast = Nvml.Read(g) ? g : null;
                cpuLast = CpuLoad();
            }
            if (fpsOverlay != null && DateTime.Now > flashUntil) fpsOverlay.Update(live, gpuLast, cpuLast, tracker.Recording, tracker.RecordedFor);
            if (anyLiveText != null && FpsPageOpen)
            {
                string t = live.Fps > 0 ? "Сейчас: " + live.Game + " — " + live.Fps + " FPS, 1% low " + live.Low1 : "Сейчас: игра не найдена — переключитесь в окно игры.";
                if (tracker.Recording) t = "● Идёт запись " + ((int)tracker.RecordedFor.TotalMinutes).ToString("00") + ":" + tracker.RecordedFor.Seconds.ToString("00") + ".  " + t;
                anyLiveText.Text = t;
            }
        }

        void StopFpsAny()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(h, HkOverlay); UnregisterHotKey(h, HkRecord);
            }
            catch { }
            if (fpsOverlay != null) { fpsOverlay.Close(); fpsOverlay = null; }
            if (anyTimer != null) anyTimer.Stop();
            if (tracker != null) { tracker.Dispose(); tracker = null; }   // синхронно: ETW-сессия не должна остаться после выхода
        }

        // ---------------------------------------------------------------- история замеров

        List<FpsRun> LoadRuns()
        {
            var list = new List<FpsRun>();
            try
            {
                if (!File.Exists(RunsPath)) return list;
                var d = J.Parse(File.ReadAllText(RunsPath, Encoding.UTF8));
                foreach (var r in J.A(d, "runs"))
                {
                    DateTime at;
                    DateTime.TryParse(J.S(r, "at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
                    list.Add(new FpsRun { Game = J.S(r, "game"), At = at, Seconds = J.N(r, "sec"), Avg = J.N(r, "avg"), Low1 = J.N(r, "low1"), Low01 = J.N(r, "low01"), Frames = (int)J.N(r, "frames") });
                }
            }
            catch { }
            return list;
        }

        void SaveRun(FpsRun run)
        {
            try
            {
                var list = LoadRuns();
                list.Insert(0, run);
                var arr = list.Take(50).Select(r => (object)new Dictionary<string, object>
                {
                    { "game", r.Game }, { "at", r.At.ToString("s", CultureInfo.InvariantCulture) }, { "sec", r.Seconds },
                    { "avg", Math.Round(r.Avg, 1) }, { "low1", Math.Round(r.Low1, 1) }, { "low01", Math.Round(r.Low01, 1) }, { "frames", r.Frames }
                }).ToArray();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RunsPath));
                File.WriteAllText(RunsPath, J.Write(new Dictionary<string, object> { { "runs", arr } }), new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------------------------------------------------------- карточки на вкладке «Замер FPS»

        static readonly string[] CornerNames = { "слева сверху", "справа сверху", "слева снизу", "справа снизу" };

        Border AnyGameCard()
        {
            var c = new StackPanel();
            c.Children.Add(T("Любая игра", 16, "Txt", FontWeights.SemiBold));
            var intro = T("Nolagi считает кадры через Windows, как PresentMon: в игру ничего не внедряется, античиты (FACEIT, VAC) это не трогает.", 13.5, "Mut");
            intro.Margin = new Thickness(0, 4, 0, 12);
            c.Children.Add(intro);

            // оверлей
            var ov = new CheckBox { Style = St("Chk"), IsChecked = cfg.OverlayOn };
            ov.Content = new TextBlock { Text = "Показывать FPS поверх игры   (Ctrl+F12)", Margin = new Thickness(8, 0, 0, 0) };
            ov.Click += (s, e) => SetOverlay(ov.IsChecked == true);
            c.Children.Add(ov);
            var cornerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 4, 0, 0) };
            var cornerBtn = Btn("Угол: " + CornerNames[cfg.OverlayCorner], "Link");
            cornerBtn.Click += (s, e) =>
            {
                cfg.OverlayCorner = (cfg.OverlayCorner + 1) % 4; cfg.Save();
                cornerBtn.Content = "Угол: " + CornerNames[cfg.OverlayCorner];
                if (fpsOverlay != null) fpsOverlay.SetCorner(cfg.OverlayCorner);
            };
            cornerRow.Children.Add(cornerBtn);
            c.Children.Add(cornerRow);

            // запись
            bool recOn = tracker != null && tracker.Recording;
            var recHead = T("Замер в своей игре", 14, "Txt", FontWeights.SemiBold);
            recHead.Margin = new Thickness(0, 16, 0, 2);
            c.Children.Add(recHead);
            c.Children.Add(T("Нажмите «Начать запись», переключитесь в игру и играйте 1–3 минуты в одном и том же месте. Остановить — Ctrl+F11 прямо в игре. Сравнивайте замеры до и после настроек.", 13.5, "Mut"));
            anyRecBtn = Btn(recOn ? "Остановить запись" : "Начать запись   (Ctrl+F11)", recOn ? "Danger" : "Primary");
            anyRecBtn.HorizontalAlignment = HorizontalAlignment.Left;
            anyRecBtn.Margin = new Thickness(0, 10, 0, 0);
            anyRecBtn.Click += (s, e) => ToggleRecording();
            c.Children.Add(anyRecBtn);
            anyLiveText = T(tracker == null ? "" : "…", 13, "Acc");
            anyLiveText.Margin = new Thickness(0, 8, 0, 0);
            c.Children.Add(anyLiveText);
            var note = T("Оверлей виден в режиме «Окно без рамки» / «Полноэкранный в окне». В эксклюзивном полноэкранном режиме Windows его прячет, но запись замера работает всегда.", 12, "Mut");
            note.Margin = new Thickness(0, 10, 0, 0);
            c.Children.Add(note);
            var card = Card(c);
            card.Margin = new Thickness(0, 0, 0, 12);
            return card;
        }

        Border AnyResultCard(FpsRun run, FpsRun prev)
        {
            var c = new StackPanel();
            c.Children.Add(T("Результат · " + run.Game + " · " + FormatSec(run.Seconds), 13, "Mut"));
            var nums = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            nums.Children.Add(Metric("Средний FPS", run.Avg, prev == null ? (double?)null : prev.Avg));
            nums.Children.Add(Metric("1% low", run.Low1, prev == null ? (double?)null : prev.Low1));
            nums.Children.Add(Metric("0.1% low", run.Low01, prev == null || prev.Low01 <= 0 ? (double?)null : prev.Low01));
            c.Children.Add(nums);
            if (run.PerSecond.Length >= 3) c.Children.Add(FpsChart(run.PerSecond));
            string hint = prev == null ? "Первый замер в этой игре. Примените настройки и повторите в том же месте — увидите разницу."
                                       : "Стрелки — разница с прошлым замером в этой игре (" + When(prev.At.ToString("s", CultureInfo.InvariantCulture)) + ").";
            if (run.Avg > 0 && run.Low1 / run.Avg < 0.4) hint += " 1% low меньше 40% от среднего — кадры идут неровно (микрофризы).";
            if (run.MaxFrameMs > 100) hint += " Самый долгий кадр — " + Math.Round(run.MaxFrameMs) + " мс: был заметный фриз.";
            var h = T(hint, 13, "Mut"); h.Margin = new Thickness(0, 10, 0, 0);
            c.Children.Add(h);
            var card = Card(c);
            card.Margin = new Thickness(0, 0, 0, 12);
            return card;
        }

        static string FormatSec(double s)
        {
            int t = (int)Math.Round(s);
            return t >= 60 ? (t / 60) + " мин " + (t % 60) + " с" : t + " с";
        }

        static FrameworkElement FpsChart(double[] per)
        {
            double max = Math.Max(30, per.Max()) * 1.1, h = 70;
            var canvas = new Canvas { Height = h, Margin = new Thickness(0, 12, 0, 0), ClipToBounds = true };
            var line = new Polyline { Stroke = Res("Acc"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var avgLine = new Line { Stroke = Res("Line"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
            canvas.Children.Add(avgLine); canvas.Children.Add(line);
            double avg = per.Average();
            canvas.SizeChanged += (s, e) =>
            {
                double w = canvas.ActualWidth, step = per.Length > 1 ? w / (per.Length - 1) : w;
                var pts = new PointCollection();
                for (int i = 0; i < per.Length; i++) pts.Add(new Point(i * step, h - h * per[i] / max));
                line.Points = pts;
                avgLine.X1 = 0; avgLine.X2 = w; avgLine.Y1 = avgLine.Y2 = h - h * avg / max;
            };
            var g = new StackPanel();
            g.Children.Add(canvas);
            g.Children.Add(new TextBlock { Text = "FPS по секундам, пунктир — средний", FontSize = 11.5, Foreground = Res("Mut") });
            return g;
        }

        Border AnyHistoryCard(List<FpsRun> runs)
        {
            var c = new StackPanel();
            c.Children.Add(T("Прошлые замеры в играх", 15, "Txt", FontWeights.SemiBold));
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var w in new[] { 1.6, 1.2, 0.8, 0.8, 0.8 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            Action<int, string[], string> addRow = (r, cells, color) =>
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < cells.Length; i++)
                {
                    var tb = new TextBlock { Text = cells[i], FontSize = 13, Foreground = Res(color), Margin = new Thickness(0, 4, 8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetRow(tb, r); Grid.SetColumn(tb, i);
                    g.Children.Add(tb);
                }
            };
            addRow(0, new[] { "Игра", "Когда", "Средний", "1% low", "Длина" }, "Mut");
            int row = 1;
            foreach (var r in runs.Take(12))
                addRow(row++, new[] { r.Game, When(r.At.ToString("s", CultureInfo.InvariantCulture)), r.Avg.ToString("0", CultureInfo.InvariantCulture), r.Low1.ToString("0", CultureInfo.InvariantCulture), FormatSec(r.Seconds) }, "Txt");
            c.Children.Add(g);
            var card = Card(c);
            card.Margin = new Thickness(0, 12, 0, 0);
            return card;
        }

        /// Карточки «любой игры» в начало вкладки «Замер FPS».
        void AddAnyGameCards(StackPanel sp)
        {
            if (anyError != null) { sp.Children.Add(Note(anyError, "Bad", "BadSoft")); anyError = null; }
            if (anyLast != null) sp.Children.Add(AnyResultCard(anyLast, anyPrev));
            sp.Children.Add(AnyGameCard());
        }

        void AddAnyHistory(StackPanel sp)
        {
            var runs = LoadRuns();
            if (runs.Count > 0) sp.Children.Add(AnyHistoryCard(runs));
        }
    }
}
