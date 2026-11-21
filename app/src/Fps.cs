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
        Task<Dictionary<string, object>> fpsTask;      // замер
        Task<Dictionary<string, object>> installTask;  // скачивание PresentMon (отдельно: его можно бросить)
        bool installAbandoned, fpsCancelling;
        string fpsPhase = "";          // wait | key | run | install
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
        bool Installing { get { return installTask != null && !installTask.IsCompleted && !installAbandoned; } }
        bool FpsPageOpen { get { return navFps != null && navFps.IsChecked == true; } }
        static string CancelFlag { get { return System.IO.Path.Combine(Payload.Root, "fps-cancel.flag"); } }

        async void ShowFps()
        {
            if (FpsBusy || Installing) { RenderFpsRunning(); return; }
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(Spinner(36));
            SetPage(PageFrame("Замер FPS", null, wait));
            try { fpsStatus = await Bridge.Run("fps-status", null, null); }
            catch (Exception ex)
            {
                if (FpsPageOpen) SetPage(PageFrame("Замер FPS", "Ошибка: " + ex.Message, new Grid()));
                return;
            }
            // результат бенчмарка CS2 мог появиться, пока вкладка была закрыта
            var bench = J.O(fpsStatus, "bench");
            if (bench != null && J.N(bench, "newCount") > 0)
            {
                var res = new Dictionary<string, object>();
                res["record"] = J.O(bench, "latest");
                res["previous"] = J.O(bench, "previous");
                res["gameName"] = "CS2 · бенчмарк";
                fpsResult = res;
            }
            if (FpsPageOpen && !FpsBusy && !Installing) RenderFpsIdle();
        }

        // ---------------------------------------------------------------- выбор игры и старт

        void RenderFpsIdle()
        {
            var sp = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            var games = J.A(fpsStatus, "games");
            bool dota = games.Any(x => J.S(x, "id") == "dota" && J.B(x, "found"));
            var bench = J.O(fpsStatus, "bench");
            fpsGame = "dota";

            if (fpsError != null)
            {
                sp.Children.Add(Note(fpsError, "Bad", "BadSoft"));
                fpsError = null;
            }
            if (fpsResult != null) sp.Children.Add(ResultCard(fpsResult));

            // ---- CS2: карта «CS2 FPS Benchmark», результат забирается из консоли
            if (bench != null && J.B(bench, "cs2")) sp.Children.Add(BenchCard(bench));

            // ---- Dota 2: замер через PresentMon
            if (dota)
            {
                var c = new StackPanel();
                c.Children.Add(T("Dota 2", 16, "Txt", FontWeights.SemiBold));
                c.Children.Add(T("Длительность", 13, "Mut"));
                ((TextBlock)c.Children[1]).Margin = new Thickness(0, 8, 0, 0);
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
                start.Click += (s, e) => StartFps();
                c.Children.Add(start);
                var how = T("Нажмите кнопку, зайдите в Dota 2 на карту — демо-режим героя или бот-матч — и нажмите F10 прямо в игре: с этого момента идёт замер. " +
                            "Повторяйте одну и ту же сцену, тогда «до» и «после» можно честно сравнить. FPS считает Intel PresentMon: он только читает события Windows о кадрах.", 13, "Mut");
                how.Margin = new Thickness(0, 12, 0, 0);
                c.Children.Add(how);
                var card = Card(c);
                card.Margin = new Thickness(0, 12, 0, 0);
                sp.Children.Add(card);
            }

            if ((bench == null || !J.B(bench, "cs2")) && !dota)
            {
                var c = new StackPanel();
                c.Children.Add(T("CS2 и Dota 2 не найдены в библиотеках Steam.", 15, "Txt", FontWeights.SemiBold));
                c.Children.Add(T("Если игра установлена — запустите её один раз через Steam и откройте вкладку снова.", 13.5, "Mut"));
                sp.Children.Add(Card(c));
            }

            var hist = J.A(fpsStatus, "history");
            if (hist.Count > 0) sp.Children.Add(HistoryCard(hist));
            SetPage(PageFrame("Замер FPS", "Средний FPS и 1% low — чтобы видеть, помогли ли настройки.", Scroll(sp)));
        }

        Border BenchCard(Dictionary<string, object> bench)
        {
            var c = new StackPanel();
            c.Children.Add(T("CS2 · карта «CS2 FPS Benchmark»", 16, "Txt", FontWeights.SemiBold));
            object cd = J.Get(bench, "condebug");
            if (!(cd is bool) || !(bool)cd)
            {
                var t = T(cd == null
                    ? "Сначала запустите CS2 хотя бы раз через Steam — тогда Nolagi сможет включить запись консоли."
                    : "Чтобы Nolagi сам забирал результат, включите запись консоли CS2 (параметр запуска -condebug, на FPS не влияет). Перед этим закройте Steam: значок у часов → Выход.", 13.5, "Mut");
                t.Margin = new Thickness(0, 6, 0, 12);
                c.Children.Add(t);
                if (cd != null)
                {
                    var on = Btn("Включить запись консоли", "Primary");
                    on.HorizontalAlignment = HorizontalAlignment.Left;
                    on.Click += async (s, e) =>
                    {
                        on.IsEnabled = false;
                        try { fpsStatus["bench"] = J.O(await Bridge.Run("bench-enable", null, null), "bench"); }
                        catch (Exception ex) { fpsError = "Не получилось: " + ex.Message; }
                        if (FpsPageOpen) RenderFpsIdle();
                    };
                    c.Children.Add(on);
                }
            }
            else
            {
                string[] steps =
                {
                    "1.  Подпишитесь на карту «CS2 FPS Benchmark» в Мастерской Steam (один раз).",
                    "2.  В CS2: Играть → Мастерская → CS2 FPS Benchmark, дождитесь конца пролёта.",
                    "3.  Вернитесь в Nolagi и нажмите «Забрать результат» — средний FPS и 1% low появятся здесь."
                };
                var list = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
                foreach (var st in steps) { var tb = T(st, 13.5, "Txt"); tb.Margin = new Thickness(0, 2, 0, 2); list.Children.Add(tb); }
                c.Children.Add(list);
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var take = Btn("Забрать результат", "Primary");
                var shop = Btn("Открыть Мастерскую");
                shop.Margin = new Thickness(8, 0, 0, 0);
                take.Click += async (s, e) =>
                {
                    take.IsEnabled = false;
                    Dictionary<string, object> b = null;
                    try { b = J.O(await Bridge.Run("bench-check", null, null), "bench"); }
                    catch (Exception ex) { fpsError = "Не получилось прочитать консоль CS2: " + ex.Message; }
                    if (b != null)
                    {
                        if (J.S(b, "readError").Length > 0)
                            fpsError = "Не удалось прочитать консоль CS2 — скорее всего, игра держит файл. Закройте CS2 и нажмите ещё раз.";
                        else if (J.N(b, "newCount") > 0)
                        {
                            var res = new Dictionary<string, object>();
                            res["record"] = J.O(b, "latest");
                            res["previous"] = J.O(b, "previous");
                            res["gameName"] = "CS2 · бенчмарк";
                            fpsResult = res;
                        }
                        else fpsError = "Нового результата нет. Пройдите карту до конца пролёта и нажмите ещё раз. Если CS2 запускалась до включения записи консоли — перезапустите игру.";
                    }
                    if (FpsPageOpen) ShowFps();   // заново: история и результат
                };
                shop.Click += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start("https://steamcommunity.com/workshop/browse/?appid=730&searchtext=CS2+FPS+Benchmark"); } catch { }
                };
                row.Children.Add(take); row.Children.Add(shop);
                c.Children.Add(row);
            }
            return Card(c);
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
            installAbandoned = false;
            var task = Bridge.Run("fps-install", null, m => Dispatcher.BeginInvoke(new Action(() => { fpsText = J.S(m, "text"); UpdateFpsLive(); })));
            installTask = task;
            if (FpsPageOpen) RenderFpsRunning();
            UpdateNavFps();
            Exception err = null;
            try { await task; }
            catch (Exception ex) { err = ex; }
            bool abandoned = installAbandoned || installTask != task;
            if (installTask == task) installTask = null;
            UpdateNavFps();
            if (abandoned) return;   // пользователь нажал «Отмена» — файл, если скачался, просто пригодится в следующий раз
            if (err != null)
            {
                fpsError = "PresentMon не установлен: " + err.Message;
                if (FpsPageOpen) RenderFpsIdle();
                return;
            }
            if (fpsStatus != null) fpsStatus["presentMon"] = true;
            RunMeasure();
        }

        async void RunMeasure()
        {
            try { File.Delete(CancelFlag); } catch { }
            fpsCancelling = false;
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
            fpsCancelling = false;
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
            if (fpsPhase == "install") { installAbandoned = true; UpdateNavFps(); RenderFpsIdle(); return; }
            try { File.WriteAllText(CancelFlag, "cancel"); } catch { }
            fpsCancelling = true;
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
            if (fpsPhase == "wait" || fpsPhase == "key" || fpsPhase == "install")
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

            var stop = Btn(fpsPhase == "install" ? "Отмена" : "Остановить");
            stop.HorizontalAlignment = HorizontalAlignment.Center;
            stop.Margin = new Thickness(0, 18, 0, 0);
            stop.IsEnabled = !fpsCancelling;
            stop.Click += (s, e) => { stop.IsEnabled = false; CancelFps(); };
            col.Children.Add(stop);
            if (fpsPhase != "install")
                col.Children.Add(new TextBlock { Text = "Замер идёт в фоне — можно открыть другие вкладки.", FontSize = 12.5, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
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
            if (fpsCancelling && fpsPhase != "install")
            {
                fpsBig.Text = ""; fpsSub.Text = "";
                fpsCaption.Text = "Останавливаю…";
                return;
            }
            switch (fpsPhase)
            {
                case "install":
                    fpsBig.Text = ""; fpsSub.Text = "";
                    fpsCaption.Text = fpsText + "…";
                    break;
                case "wait":
                    fpsBig.Text = "";
                    fpsSub.Text = "жду игру";
                    fpsCaption.Text = "Запустите " + game + ".";
                    break;
                case "key":
                    fpsBig.Text = "F10";
                    fpsSub.Text = "для старта";
                    fpsCaption.Text = "Зайдите в " + game + " на карту — демо-режим героя или бот-матч — и нажмите F10 прямо в игре. Замер начнётся сразу.";
                    break;
                case "run":
                    fpsBig.Text = fpsLive > 0 ? fpsLive.ToString("0", CultureInfo.InvariantCulture) : "—";
                    fpsSub.Text = "FPS сейчас";
                    int left = (int)Math.Max(0, fpsN - fpsI);
                    fpsCaption.Text = "Идёт замер " + game + " · осталось " + left + " сек";
                    SetArc(fpsI / fpsN);
                    break;
            }
        }

        // значок на вкладке: точка, пока идёт замер
        void UpdateNavFps()
        {
            if (navFps == null) return;
            navFps.Opacity = 1;
            bool busy = FpsBusy || Installing;
            navFps.ToolTip = busy ? "Идёт замер FPS" : null;
            var sp = navFps.Content as StackPanel;
            if (sp == null) return;
            if (sp.Children.Count > 2) sp.Children.RemoveAt(2);
            if (busy)
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
            if (J.N(rec, "low01") > 0) nums.Children.Add(Metric("0.1% low", J.N(rec, "low01"), prev == null ? (double?)null : J.N(prev, "low01")));
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
                    J.S(h, "game") == "dota" ? "Dota 2" : (J.S(h, "source") == "bench" ? "CS2 · бенчмарк" : "CS2"),
                    J.N(h, "avg").ToString("0", CultureInfo.InvariantCulture),
                    J.N(h, "low1").ToString("0", CultureInfo.InvariantCulture),
                    J.N(h, "seconds") > 0 ? J.N(h, "seconds").ToString("0", CultureInfo.InvariantCulture) : "—"
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
