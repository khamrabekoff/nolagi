// Nolagi.exe - вкладка «Мониторинг»: видеокарта, процессор, память и топ процессов в реальном времени.
// Данные собираются только пока вкладка открыта (nvidia-smi и счётчики Windows), без драйверов и фоновых служб.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Pckit
{
    public partial class MainWindow
    {
        RadioButton navMon;
        LiveMonitor mon;
        DispatcherTimer monTimer;
        bool monBusy;

        class Spark
        {
            public Polyline Line;
            public Queue<double> Data = new Queue<double>();
            public double Max = 100;
        }

        TextBlock mGpuBig, mGpuSub, mCpuBig, mCpuSub, mRamBig, mRamSub, mReason;
        Spark sGpu, sCpu, sRam, sTemp;
        Dictionary<string, TextBlock> mGpuVals;
        WrapPanel mCores;
        StackPanel mTop, mWarn;
        UIElement mGpuCard;

        bool MonPageOpen { get { return navMon != null && navMon.IsChecked == true; } }

        void ShowMonitor()
        {
            StopMonitor();
            var outer = new StackPanel();
            mWarn = new StackPanel();
            outer.Children.Add(mWarn);

            // ---- три главные плитки
            var tiles = new UniformGrid3();
            Border t1, t2, t3;
            t1 = MonTile("Видеокарта", out mGpuBig, out mGpuSub, out sGpu, "Acc");
            t2 = MonTile("Процессор", out mCpuBig, out mCpuSub, out sCpu, "Ok");
            t3 = MonTile("Память", out mRamBig, out mRamSub, out sRam, "Warn");
            tiles.Add(t1); tiles.Add(t2); tiles.Add(t3);
            outer.Children.Add(tiles.Grid);

            // ---- видеокарта подробно
            var gsp = new StackPanel();
            gsp.Children.Add(SectionHead("Видеокарта", "Данные драйвера NVIDIA (nvidia-smi), обновляются раз в секунду.", true));
            var grid = new Grid();
            for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
            mGpuVals = new Dictionary<string, TextBlock>();
            string[,] cells = { { "temp", "Температура" }, { "clock", "Частота ядра" }, { "mem", "Частота памяти" }, { "fan", "Вентилятор" },
                                { "power", "Потребление" }, { "limit", "Лимит питания" }, { "vram", "Видеопамять" }, { "load", "Загрузка" } };
            for (int i = 0; i < 8; i++)
            {
                var c = new StackPanel { Margin = new Thickness(0, 6, 12, 6) };
                c.Children.Add(new TextBlock { Text = cells[i, 1], FontSize = 12, Foreground = Res("Mut") });
                var v = new TextBlock { Text = "—", FontSize = 18, FontWeight = FontWeights.SemiBold };
                c.Children.Add(v);
                mGpuVals[cells[i, 0]] = v;
                Grid.SetColumn(c, i % 4); Grid.SetRow(c, i / 4);
                grid.Children.Add(c);
            }
            gsp.Children.Add(grid);
            var tempHead = new TextBlock { Text = "Температура за минуту", FontSize = 12, Foreground = Res("Mut"), Margin = new Thickness(0, 8, 0, 4) };
            gsp.Children.Add(tempHead);
            sTemp = new Spark { Max = 100 };
            gsp.Children.Add(SparkBox(sTemp, "Bad", 54));
            mReason = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            gsp.Children.Add(mReason);
            mGpuCard = Card(gsp, new Thickness(20, 12, 20, 16));
            ((FrameworkElement)mGpuCard).Margin = new Thickness(0, 14, 8, 0);
            mGpuCard.Visibility = Visibility.Collapsed;
            outer.Children.Add(mGpuCard);

            // ---- ядра процессора
            var csp = new StackPanel();
            csp.Children.Add(SectionHead("Ядра процессора", "Если одно ядро постоянно на 100%, а остальные отдыхают — игра упирается в процессор.", true));
            mCores = new WrapPanel();
            csp.Children.Add(mCores);
            var cc = Card(csp, new Thickness(20, 12, 20, 16)); cc.Margin = new Thickness(0, 14, 8, 0);
            outer.Children.Add(cc);

            // ---- топ процессов
            var psp = new StackPanel();
            psp.Children.Add(SectionHead("Кто нагружает компьютер", "Самые прожорливые программы сейчас (несколько окон одной программы сложены).", true));
            mTop = new StackPanel();
            psp.Children.Add(mTop);
            var pc = Card(psp, new Thickness(20, 12, 20, 16)); pc.Margin = new Thickness(0, 14, 8, 0);
            outer.Children.Add(pc);

            outer.Children.Add(new TextBlock
            {
                Text = "Температуру процессора Nolagi не показывает: для неё нужен драйвер ядра, а Nolagi принципиально их не ставит (FACEIT и античиты относятся к таким драйверам с подозрением). Для температуры процессора используйте HWiNFO.",
                FontSize = 12, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 12, 4)
            });

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outer };
            SetPage(PageFrame("Мониторинг", "Видеокарта, процессор и память в реальном времени. Работает, только пока открыта эта вкладка.", sv));

            mon = new LiveMonitor();
            mon.Start();
            monTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            monTimer.Tick += async (s, e) => await MonTick();
            monTimer.Start();
            var _ = MonTick();
        }

        void StopMonitor()
        {
            if (monTimer != null) { monTimer.Stop(); monTimer = null; }
            var m = mon; mon = null;
            if (m != null) Task.Run(() => m.Dispose());
        }

        async Task MonTick()
        {
            var m = mon;
            if (m == null || monBusy) return;
            monBusy = true;
            MonSample s = null;
            try { s = await Task.Run(() => m.Sample()); }
            catch { }
            finally { monBusy = false; }
            if (s == null || mon != m || !MonPageOpen) return;
            RenderMon(s);
        }

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }

        void RenderMon(MonSample s)
        {
            // плитка видеокарты
            if (s.GpuLoad >= 0)
            {
                mGpuBig.Text = F0(s.GpuLoad) + "%";
                mGpuSub.Text = s.Nvidia ? (s.GpuTemp >= 0 ? F0(s.GpuTemp) + " °C · " : "") + (s.GpuClock >= 0 ? F0(s.GpuClock) + " МГц" : "") : "загрузка (температура — только у NVIDIA)";
                Push(sGpu, s.GpuLoad);
            }
            else { mGpuBig.Text = "—"; mGpuSub.Text = "нет данных о видеокарте"; }

            // процессор
            if (s.CpuTotal >= 0)
            {
                mCpuBig.Text = F0(s.CpuTotal) + "%";
                mCpuSub.Text = (s.CpuMhz > 0 ? F0(s.CpuMhz) + " МГц · " : "") + s.Cores.Length + " потоков";
                Push(sCpu, s.CpuTotal);
            }

            // память
            if (s.RamTotalGb > 0)
            {
                double pct = s.RamUsedGb / s.RamTotalGb * 100;
                mRamBig.Text = F0(pct) + "%";
                mRamSub.Text = F1(s.RamUsedGb) + " из " + F0(Math.Round(s.RamTotalGb)) + " ГБ";
                Push(sRam, pct);
            }

            // подробно по NVIDIA
            if (s.Nvidia)
            {
                mGpuCard.Visibility = Visibility.Visible;
                Action<string, double, string> set = (k, v, unit) => mGpuVals[k].Text = v >= 0 ? F0(v) + unit : "—";
                set("temp", s.GpuTemp, " °C"); set("clock", s.GpuClock, " МГц"); set("mem", s.GpuMemClock, " МГц");
                set("fan", s.GpuFan, "%"); set("power", s.GpuPower, " Вт"); set("limit", s.GpuPowerLimit, " Вт"); set("load", s.GpuLoad, "%");
                mGpuVals["vram"].Text = s.GpuMemUsed >= 0 && s.GpuMemTotal > 0 ? F1(s.GpuMemUsed / 1024) + " / " + F0(Math.Round(s.GpuMemTotal / 1024)) + " ГБ" : "—";
                mGpuVals["temp"].Foreground = s.GpuTemp >= 83 ? Res("Bad") : s.GpuTemp >= 75 ? Res("Warn") : Res("Txt");
                if (s.GpuTemp >= 0) Push(sTemp, s.GpuTemp);
                string why = LiveMonitor.GpuReasonText(s.GpuReasons, s.GpuLoad);
                mReason.Text = why.Length > 0 ? "Почему такая частота: " + why : "";
                mReason.Foreground = s.GpuReasons >= 0 && (s.GpuReasons & 0xE8) != 0 ? Res("Bad") : Res("Mut");
            }

            // ядра
            if (mCores.Children.Count != s.Cores.Length)
            {
                mCores.Children.Clear();
                for (int i = 0; i < s.Cores.Length; i++) mCores.Children.Add(CoreBar(i));
            }
            for (int i = 0; i < s.Cores.Length; i++)
            {
                var g = (Grid)mCores.Children[i];
                var fill = (Border)g.Children[1];
                var lbl = (TextBlock)g.Children[2];
                double v = s.Cores[i];
                fill.Height = Math.Max(2, 56 * v / 100);
                fill.Background = v >= 95 ? Res("Bad") : v >= 70 ? Res("Warn") : Res("Ok");
                lbl.Text = F0(v);
            }

            // процессы
            mTop.Children.Clear();
            foreach (var p in s.Top)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                var n = new TextBlock { Text = p.Item1, TextTrimming = TextTrimming.CharacterEllipsis };
                var c = new TextBlock { Text = "ЦП " + F1(p.Item2) + "%", Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Right };
                var r = new TextBlock { Text = (p.Item3 >= 1024 ? F1(p.Item3 / 1024) + " ГБ" : F0(p.Item3) + " МБ"), Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(c, 1); Grid.SetColumn(r, 2);
                row.Children.Add(n); row.Children.Add(c); row.Children.Add(r);
                mTop.Children.Add(row);
            }

            // предупреждения
            var warns = new List<string>();
            if (s.GpuTemp >= 83) warns.Add("Видеокарта горячая: " + F0(s.GpuTemp) + " °C. Выше 83 °C она начинает сбрасывать частоты — почистите от пыли, проверьте вентиляторы и обдув корпуса.");
            if (s.RamTotalGb > 0 && s.RamUsedGb / s.RamTotalGb >= 0.9) warns.Add("Память почти закончилась (" + F0(s.RamUsedGb / s.RamTotalGb * 100) + "%). Закройте лишние программы и вкладки браузера — иначе в игре будут фризы.");
            if (s.Cores.Length > 1 && s.Cores.Max() >= 98 && s.CpuTotal < 60) warns.Add("Одно ядро процессора загружено на 100%, остальные свободны — игра упирается в процессор. Видеокарта тут не поможет.");
            mWarn.Children.Clear();
            foreach (var w in warns)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var ic = Glyph("", 18, "Warn"); ic.Margin = new Thickness(0, 0, 12, 0); ic.VerticalAlignment = VerticalAlignment.Top;
                var t = new TextBlock { Text = w, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(t, 1);
                g.Children.Add(ic); g.Children.Add(t);
                mWarn.Children.Add(new Border { Background = Res("WarnSoft"), BorderBrush = Res("Warn"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 8, 10), Child = g });
            }
        }

        // ---- элементы

        class UniformGrid3
        {
            public Grid Grid = new Grid();
            int n;
            public UniformGrid3() { for (int i = 0; i < 3; i++) Grid.ColumnDefinitions.Add(new ColumnDefinition()); }
            public void Add(FrameworkElement e) { e.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(e, n++); Grid.Children.Add(e); }
        }

        Border MonTile(string title, out TextBlock big, out TextBlock sub, out Spark spark, string color)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Res("Mut") });
            big = new TextBlock { Text = "…", FontSize = 30, FontWeight = FontWeights.SemiBold };
            sp.Children.Add(big);
            sub = new TextBlock { Text = " ", FontSize = 12, Foreground = Res("Mut"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(sub);
            spark = new Spark();
            sp.Children.Add(SparkBox(spark, color, 44));
            return Card(sp, new Thickness(18, 12, 18, 14));
        }

        static FrameworkElement SparkBox(Spark s, string color, double height)
        {
            var c = new Canvas { Height = height, ClipToBounds = true, Background = Brushes.Transparent };
            c.Children.Add(new Line { X1 = 0, X2 = 4000, Y1 = height - 0.5, Y2 = height - 0.5, Stroke = Res("Line"), StrokeThickness = 1 });
            s.Line = new Polyline { Stroke = Res(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            c.Children.Add(s.Line);
            c.SizeChanged += (o, e) => Redraw(s);
            return c;
        }

        static void Push(Spark s, double v)
        {
            s.Data.Enqueue(Math.Max(0, Math.Min(s.Max, v)));
            while (s.Data.Count > 60) s.Data.Dequeue();
            Redraw(s);
        }

        static void Redraw(Spark s)
        {
            var c = s.Line.Parent as Canvas;
            if (c == null || c.ActualWidth <= 0) return;
            double w = c.ActualWidth, h = c.ActualHeight - 2, step = w / 59;
            var pts = new PointCollection();
            int i = 60 - s.Data.Count;
            foreach (var v in s.Data) { pts.Add(new Point(i * step, 1 + h - h * v / s.Max)); i++; }
            s.Line.Points = pts;
        }

        Grid CoreBar(int i)
        {
            var g = new Grid { Width = 30, Margin = new Thickness(0, 4, 6, 4) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var track = new Border { Background = Res("Bg"), CornerRadius = new CornerRadius(4) };
            var fill = new Border { CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Bottom, Height = 2, Background = Res("Ok") };
            var lbl = new TextBlock { FontSize = 11, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center, Text = "0" };
            Grid.SetRow(lbl, 1);
            g.Children.Add(track); g.Children.Add(fill); g.Children.Add(lbl);
            g.ToolTip = "Поток " + (i + 1);
            return g;
        }
    }
}
