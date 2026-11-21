// Nolagi.exe - «Замер FPS»: отдельная вкладка, замер идёт в фоне, программой можно пользоваться.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Pckit
{
    public partial class MainWindow
    {
        RadioButton navFps;

        // состояние замера живёт отдельно от страницы: ушли на другую вкладку и вернулись — видно, что идёт
        Task<Dictionary<string, object>> fpsTask;
        string fpsPhase = "";          // wait | countdown | run | install
        double fpsI, fpsN, fpsLive;
        int fpsLeft;
        string fpsText = "";
        string fpsGame = "cs2";
        int fpsSeconds = 60;
        Dictionary<string, object> fpsResult;   // последний замер в этой сессии
        string fpsError;
        Dictionary<string, object> fpsStatus;

        // живые элементы анимации (обновляются, пока открыта вкладка)
        TextBlock fpsBig, fpsSub, fpsCaption;
        Ellipse fpsArc;
        const double RingSize = 190, RingStroke = 12;

        bool FpsBusy { get { return fpsTask != null && !fpsTask.IsCompleted; } }
        bool FpsPageOpen { get { return navFps != null && navFps.IsChecked == true; } }
        static string CancelFlag { get { return System.IO.Path.Combine(Payload.Root, "fps-cancel.flag"); } }

        async void ShowFps()
        {
            if (FpsBusy) { RenderFpsRunning(); return; }
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(Spinner(36));
            SetPage(PageFrame("Замер FPS", null, wait));
            try { fpsStatus = await Bridge.Run("fps-status", null, null); }
            catch (Exception ex)
            {
                if (FpsPageOpen) SetPage(PageFrame("Замер FPS", "Ошибка: " + ex.Message, new Grid()));
                return;
            }
            if (FpsPageOpen && !FpsBusy) RenderFpsIdle();
        }

        // ---------------------------------------------------------------- выбор игры и старт

        void RenderFpsIdle()
        {
            var sp = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            var games = J.A(fpsStatus, "games");
            var found = games.Where(x => J.B(x, "found")).ToList();
            if (found.Count > 0 && !found.Any(x => J.S(x, "id") == fpsGame)) fpsGame = J.S(found[0], "id");

            if (fpsError != null)
            {
                sp.Children.Add(Note(fpsError, "Bad", "BadSoft"));
                fpsError = null;
            }
            if (fpsResult != null) sp.Children.Add(ResultCard(fpsResult));

            var c = new StackPanel();
            if (found.Count == 0)
            {
                c.Children.Add(T("CS2 и Dota 2 не найдены в библиотеках Steam.", 15, "Txt", FontWeights.SemiBold));
                c.Children.Add(T("Замер работает с этими играми. Если игра установлена — запустите её один раз через Steam и откройте вкладку снова.", 13.5, "Mut"));
                sp.Children.Add(Card(c));
                SetPage(PageFrame("Замер FPS", "Средний FPS и 1% low в вашей игре — чтобы видеть, помогли ли настройки.", Scroll(sp)));
                return;
            }

            c.Children.Add(T("Игра", 13, "Mut"));
            var gameRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 14) };
            foreach (var g in found)
            {
                string id = J.S(g, "id");
                var b = Btn(J.S(g, "name") + (J.B(g, "running") ? "  · запущена" : ""), id == fpsGame ? "Primary" : "Btn");
                b.Margin = new Thickness(0, 0, 8, 0);
                b.Click += (s, e) => { fpsGame = id; RenderFpsIdle(); };
                gameRow.Children.Add(b);
            }
            c.Children.Add(gameRow);

            c.Children.Add(T("Длительность", 13, "Mut"));
            var durRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 14) };
            foreach (int sec in new[] { 30, 60, 120 })
            {
                int v = sec;
                var b = Btn(sec + " сек", sec == fpsSeconds ? "Primary" : "Btn");
                b.Margin = new Thickness(0, 0, 8, 0);
                b.Click += (s, e) => { fpsSeconds = v; RenderFpsIdle(); };
                durRow.Children.Add(b);
            }
            c.Children.Add(durRow);

            var start = Btn("Начать замер", "Primary");
            start.HorizontalAlignment = HorizontalAlignment.Left;
            start.Padding = new Thickness(22, 10, 22, 10);
            start.Click += (s, e) => StartFps();
            c.Children.Add(start);
            c.Children.Add(T("Как это работает: нажмите кнопку, запустите игру (если ещё не запущена) и зайдите на карту — бот-матч, практика или демка. " +
                             "Через 5 секунд начнётся замер. Повторяйте одну и ту же сцену, тогда «до» и «после» можно честно сравнить.", 13, "Mut"));
            ((TextBlock)c.Children[c.Children.Count - 1]).Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(Card(c));

            if (J.B(fpsStatus, "faceit"))
                sp.Children.Add(Note("Запущен клиент FACEIT. Не меряйте во время матча FACEIT — только практика, боты или демка.", "Warn", "WarnSoft"));

            var hist = J.A(fpsStatus, "history");
            if (hist.Count > 0) sp.Children.Add(HistoryCard(hist));

            sp.Children.Add(T("FPS считает Intel PresentMon: он только читает события Windows о кадрах, в игру ничего не внедряет. Замер идёт в фоне — можно пользоваться программой.", 12.5, "Mut"));
            ((TextBlock)sp.Children[sp.Children.Count - 1]).Margin = new Thickness(4, 12, 0, 0);
            SetPage(PageFrame("Замер FPS", "Средний FPS и 1% low в вашей игре — чтобы видеть, помогли ли настройки.", Scroll(sp)));
        }

        void StartFps()
        {
            if (FpsBusy) return;
            if (!J.B(fpsStatus, "presentMon")) { RenderInstallAsk(); return; }
            RunMeasure();
        }

        // PresentMon скачивается только после явного «Скачать» — без всплывающих окон
        void RenderInstallAsk()
        {
            var c = new StackPanel();
            c.Children.Add(T("Нужна программа Intel PresentMon", 16, "Txt", FontWeights.SemiBold));
            c.Children.Add(T("Бесплатная, с открытым кодом (MIT), около 2–5 МБ. Скачивается один раз из официального репозитория github.com/GameTechDev/PresentMon " +
                             "и проверяется по цифровой подписи Intel — без подписи файл удаляется.", 13.5, "Mut"));
            ((TextBlock)c.Children[1]).Margin = new Thickness(0, 6, 0, 14);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var yes = Btn("Скачать и начать замер", "Primary");
            var no = Btn("Отмена");
            no.Margin = new Thickness(8, 0, 0, 0);
            yes.Click += (s, e) => InstallThenMeasure();
            no.Click += (s, e) => RenderFpsIdle();
            row.Children.Add(yes); row.Children.Add(no);
            c.Children.Add(row);
            var sp = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(Card(c));
            SetPage(PageFrame("Замер FPS", null, sp));
        }

        async void InstallThenMeasure()
        {
            fpsPhase = "install"; fpsText = "Скачиваю PresentMon"; fpsLive = 0;
            var task = Bridge.Run("fps-install", null, m => Dispatcher.BeginInvoke(new Action(() => { fpsText = J.S(m, "text"); UpdateFpsLive(); })));
            fpsTask = task;
            if (FpsPageOpen) RenderFpsRunning();
            try { await task; }
            catch (Exception ex)
            {
                fpsTask = null;
                fpsError = "PresentMon не установлен: " + ex.Message;
                if (FpsPageOpen) RenderFpsIdle();
                return;
            }
            fpsTask = null;
            fpsStatus["presentMon"] = true;
            RunMeasure();
        }

        async void RunMeasure()
        {
            try { File.Delete(CancelFlag); } catch { }
            fpsPhase = "wait"; fpsI = 0; fpsN = 1; fpsLive = 0; fpsLeft = 0;
            fpsText = "";
            var task = Bridge.Run("fps-measure", new[] { fpsGame, fpsSeconds.ToString(CultureInfo.InvariantCulture) },
                m => Dispatcher.BeginInvoke(new Action(() => OnFpsMsg(m))));
            fpsTask = task;
            if (FpsPageOpen) RenderFpsRunning();
            UpdateNavFps();

            Dictionary<string, object> res = null;
            try { res = await task; }
            catch (Exception ex) { fpsError = "Замер не получился: " + ex.Message; }
            fpsTask = null;
            try { File.Delete(CancelFlag); } catch { }
            UpdateNavFps();
            if (res != null && !J.B(res, "cancelled")) fpsResult = res;

            // обновить историю и показать результат, если вкладка открыта
            if (FpsPageOpen) ShowFps();
            else if (res != null && !J.B(res, "cancelled"))
            {
                var rec = J.O(res, "record");
                ShowToast(string.Format(CultureInfo.InvariantCulture, "Замер FPS готов: {0} — средний {1:0}, 1% low {2:0}",
                    J.S(res, "gameName"), J.N(rec, "avg"), J.N(rec, "low1")));
            }
        }

        void OnFpsMsg(Dictionary<string, object> m)
        {
            string ph = J.S(m, "phase");
            if (ph.Length == 0) return;
            bool changed = ph != fpsPhase;
            fpsPhase = ph;
            fpsI = J.N(m, "i"); fpsN = Math.Max(1, J.N(m, "n"));
            fpsLive = J.N(m, "fps");
            fpsLeft = (int)J.N(m, "left");
            fpsText = J.S(m, "text");
            if (!FpsPageOpen) return;
            if (changed) RenderFpsRunning(); else UpdateFpsLive();
        }

        void CancelFps()
        {
            try { File.WriteAllText(CancelFlag, "cancel"); } catch { }
            fpsText = "Останавливаю…";
            UpdateFpsLive();
        }

        // ---------------------------------------------------------------- анимация процесса

        void RenderFpsRunning()
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var ring = new Grid { Width = RingSize, Height = RingSize, Margin = new Thickness(0, 10, 0, 18) };

            // мягкая пульсация фона — видно, что программа жива
            var glow = new Ellipse { Fill = Res("AccSoft"), RenderTransformOrigin = new Point(0.5, 0.5) };
            var st = new ScaleTransform(1, 1);
            glow.RenderTransform = st;
            var pulse = new DoubleAnimation(0.86, 1.0, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            ring.Children.Add(glow);

            ring.Children.Add(new Ellipse { Stroke = Res("Line"), StrokeThickness = RingStroke, Margin = new Thickness(6) });

            fpsArc = new Ellipse
            {
                Stroke = Res("Acc"), StrokeThickness = RingStroke, Margin = new Thickness(6),
                StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-90)
            };
            ring.Children.Add(fpsArc);
            if (fpsPhase == "wait" || fpsPhase == "install")
            {
                // ожидание: бегущая дуга
                var rt = new RotateTransform(-90);
                fpsArc.RenderTransform = rt;
                rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-90, 270, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
                SetArc(0.22);
            }

            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            fpsBig = new TextBlock { FontSize = 46, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            fpsSub = new TextBlock { FontSize = 13, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center };
            center.Children.Add(fpsBig); center.Children.Add(fpsSub);
            ring.Children.Add(center);
            col.Children.Add(ring);

            fpsCaption = new TextBlock { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            col.Children.Add(fpsCaption);

            if (fpsPhase != "install")
            {
                var stop = Btn("Остановить");
                stop.HorizontalAlignment = HorizontalAlignment.Center;
                stop.Margin = new Thickness(0, 18, 0, 0);
                stop.Click += (s, e) => { stop.IsEnabled = false; CancelFps(); };
                col.Children.Add(stop);
                col.Children.Add(new TextBlock { Text = "Замер идёт в фоне — можно открыть другие вкладки.", FontSize = 12.5, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
            }
            SetPage(PageFrame("Замер FPS", null, col));
            UpdateFpsLive();
        }

        void SetArc(double frac)
        {
            if (fpsArc == null) return;
            frac = Math.Max(0.001, Math.Min(1, frac));
            double d = RingSize - 12;
            double units = Math.PI * (d - RingStroke) / RingStroke;   // длина окружности в толщинах линии
            fpsArc.StrokeDashArray = new DoubleCollection { units * frac, units * 2 };
        }

        void UpdateFpsLive()
        {
            if (fpsBig == null || !FpsPageOpen) return;
            string game = fpsGame == "dota" ? "Dota 2" : "CS2";
            switch (fpsPhase)
            {
                case "install":
                    fpsBig.Text = ""; fpsSub.Text = "";
                    fpsCaption.Text = fpsText + "…";
                    break;
                case "wait":
                    fpsBig.Text = "";
                    fpsSub.Text = "жду игру";
                    fpsCaption.Text = fpsText == "Останавливаю…" ? fpsText : "Запустите " + game + " и зайдите на карту — замер начнётся сам.";
                    break;
                case "countdown":
                    fpsBig.Text = fpsLeft.ToString(CultureInfo.InvariantCulture);
                    fpsSub.Text = "до старта";
                    fpsCaption.Text = fpsText == "Останавливаю…" ? fpsText : "Переключитесь в игру и играйте как обычно.";
                    SetArc(fpsI / fpsN);
                    break;
                case "run":
                    fpsBig.Text = fpsLive > 0 ? fpsLive.ToString("0", CultureInfo.InvariantCulture) : "—";
                    fpsSub.Text = "FPS сейчас";
                    int left = (int)Math.Max(0, fpsN - fpsI);
                    fpsCaption.Text = fpsText == "Останавливаю…" ? fpsText : "Идёт замер " + game + " · осталось " + left + " сек";
                    SetArc(fpsI / fpsN);
                    break;
            }
        }

        // значок на вкладке: точка, пока идёт замер
        void UpdateNavFps()
        {
            if (navFps == null) return;
            navFps.Opacity = 1;
            navFps.ToolTip = FpsBusy ? "Идёт замер FPS" : null;
            var sp = navFps.Content as StackPanel;
            if (sp == null) return;
            if (sp.Children.Count > 2) sp.Children.RemoveAt(2);
            if (FpsBusy)
            {
                var dot = new Ellipse { Width = 8, Height = 8, Fill = Res("Acc"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                sp.Children.Add(dot);
            }
        }

        // ---------------------------------------------------------------- результат и история

        Border ResultCard(Dictionary<string, object> res)
        {
            var rec = J.O(res, "record");
            var prev = J.O(res, "previous");
            var c = new StackPanel();
            c.Children.Add(T("Результат · " + J.S(res, "gameName"), 13, "Mut"));
            var nums = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            nums.Children.Add(Metric("Средний FPS", J.N(rec, "avg"), prev == null ? (double?)null : J.N(prev, "avg")));
            nums.Children.Add(Metric("1% low", J.N(rec, "low1"), prev == null ? (double?)null : J.N(prev, "low1")));
            nums.Children.Add(Metric("0.1% low", J.N(rec, "low01"), prev == null ? (double?)null : J.N(prev, "low01")));
            c.Children.Add(nums);

            double avg = J.N(rec, "avg"), low = J.N(rec, "low1");
            string hint;
            if (prev == null) hint = "Это первый замер. Примените настройки, перезагрузите ПК и замерьте ту же сцену ещё раз — увидите разницу.";
            else hint = "Стрелки — разница с прошлым замером (" + When(J.S(prev, "at")) + ").";
            if (avg > 0 && low / avg < 0.4)
                hint += " 1% low меньше 40% от среднего — кадры идут неровно (микрофризы). Обычно помогает XMP/EXPO в BIOS, ограничение fps_max и закрытый браузер.";
            var h = T(hint, 13, "Mut"); h.Margin = new Thickness(0, 10, 0, 0);
            c.Children.Add(h);
            var card = Card(c);
            card.Margin = new Thickness(0, 0, 0, 12);
            return card;
        }

        static FrameworkElement Metric(string label, double value, double? before)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 34, 0) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Res("Mut") });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = value.ToString("0", CultureInfo.InvariantCulture), FontSize = 32, FontWeight = FontWeights.SemiBold });
            if (before.HasValue && before.Value > 0)
            {
                double pct = (value - before.Value) / before.Value * 100;
                string sign = pct >= 0 ? "▲ +" : "▼ ";
                var d = new TextBlock
                {
                    Text = sign + pct.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                    FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 7), VerticalAlignment = VerticalAlignment.Bottom,
                    Foreground = Math.Abs(pct) < 2 ? Res("Mut") : (pct > 0 ? Res("Ok") : Res("Bad"))
                };
                row.Children.Add(d);
            }
            sp.Children.Add(row);
            return sp;
        }

        Border HistoryCard(List<Dictionary<string, object>> hist)
        {
            var c = new StackPanel();
            c.Children.Add(T("Прошлые замеры", 15, "Txt", FontWeights.SemiBold));
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var w in new[] { 2.2, 1.2, 1, 1, 1 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            Action<int, string[], string> addRow = (r, cells, color) =>
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < cells.Length; i++)
                {
                    var tb = new TextBlock { Text = cells[i], FontSize = 13, Foreground = Res(color), Margin = new Thickness(0, 4, 8, 4) };
                    Grid.SetRow(tb, r); Grid.SetColumn(tb, i);
                    g.Children.Add(tb);
                }
            };
            addRow(0, new[] { "Когда", "Игра", "Средний", "1% low", "Секунд" }, "Mut");
            int row = 1;
            foreach (var h in hist.Take(10))
            {
                addRow(row++, new[]
                {
                    When(J.S(h, "at")),
                    J.S(h, "game") == "dota" ? "Dota 2" : "CS2",
                    J.N(h, "avg").ToString("0", CultureInfo.InvariantCulture),
                    J.N(h, "low1").ToString("0", CultureInfo.InvariantCulture),
                    J.N(h, "seconds").ToString("0", CultureInfo.InvariantCulture)
                }, "Txt");
            }
            c.Children.Add(g);
            var card = Card(c);
            card.Margin = new Thickness(0, 12, 0, 0);
            return card;
        }

        static string When(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return iso;
            if (t.Date == DateTime.Today) return "сегодня " + t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "вчера " + t.ToString("HH:mm");
            return t.ToString("dd.MM HH:mm");
        }

        static Border Note(string text, string fg, string bg)
        {
            return new Border
            {
                Background = Res(bg), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock { Text = text, Foreground = Res(fg), TextWrapping = TextWrapping.Wrap, FontSize = 13.5 }
            };
        }

        static ScrollViewer Scroll(UIElement content)
        {
            return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        }

        // всплывающая плашка в правом нижнем углу на 6 секунд
        void ShowToast(string text)
        {
            var b = new Border
            {
                Background = Res("Card"), BorderBrush = Res("Acc"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 24, 24), Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock { Text = text + "  →", FontSize = 14 }
            };
            b.MouseLeftButtonUp += (s, e) => { root.Children.Remove(b); navFps.IsChecked = true; };
            root.Children.Add(b);
            FadeIn(b, 200);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (s, e) => { timer.Stop(); root.Children.Remove(b); };
            timer.Start();
        }
    }
}
