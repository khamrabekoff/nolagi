// Nolagi.exe - «Замер FPS»: CS2, карта «CS2 FPS Benchmark», результат берётся из консоли игры.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Pckit
{
    public partial class MainWindow
    {
        RadioButton navFps;

        Dictionary<string, object> fpsResult;   // последний результат в этой сессии
        string fpsError;
        Dictionary<string, object> fpsStatus;

        bool FpsPageOpen { get { return navFps != null && navFps.IsChecked == true; } }

        async void ShowFps()
        {
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(Spinner(36));
            SetPage(PageFrame("Замер FPS", null, wait));
            try { fpsStatus = await Bridge.Run("fps-status", null, null); }
            catch (Exception ex)
            {
                if (FpsPageOpen) SetPage(PageFrame("Замер FPS", "Ошибка: " + ex.Message, new Grid()));
                return;
            }
            // результат бенчмарка мог появиться, пока вкладка была закрыта
            TakeBench(J.O(fpsStatus, "bench"), false);
            if (FpsPageOpen) RenderFpsIdle();
        }

        // новые результаты из ответа моста -> карточка результата; loud = показать ошибку, если нового нет или лог не прочитан
        void TakeBench(Dictionary<string, object> b, bool loud)
        {
            if (b == null) return;
            string readError = J.S(b, "readError");
            if (readError.Length > 0)
            {
                if (loud) fpsError = "Не удалось прочитать консоль CS2: " + readError;
            }
            else if (J.N(b, "newCount") > 0)
            {
                var res = new Dictionary<string, object>();
                res["record"] = J.O(b, "latest");
                res["previous"] = J.O(b, "previous");
                res["gameName"] = "CS2 · бенчмарк";
                fpsResult = res;
            }
            else if (loud)
                fpsError = "Нового результата нет. Пройдите карту до конца пролёта и нажмите ещё раз. Если CS2 запускалась до включения записи консоли — перезапустите игру.";
        }

        void RenderFpsIdle()
        {
            var sp = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            var bench = J.O(fpsStatus, "bench");

            if (fpsError != null)
            {
                sp.Children.Add(Note(fpsError, "Bad", "BadSoft"));
                fpsError = null;
            }
            if (fpsResult != null) sp.Children.Add(ResultCard(fpsResult));

            if (bench != null && J.B(bench, "cs2")) sp.Children.Add(BenchCard(bench));
            else
            {
                var c = new StackPanel();
                c.Children.Add(T("CS2 не найдена в библиотеках Steam.", 15, "Txt", FontWeights.SemiBold));
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
                        try
                        {
                            var r = await Bridge.Run("bench-enable", null, null);
                            fpsStatus["bench"] = J.O(r, "bench");
                            if (J.S(r, "warn").Length > 0) fpsError = J.S(r, "warn");
                        }
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
                    try { TakeBench(J.O(await Bridge.Run("bench-check", null, null), "bench"), true); }
                    catch (Exception ex) { fpsError = "Не получилось прочитать консоль CS2: " + ex.Message; }
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
            c.Children.Add(nums);

            double avg = J.N(rec, "avg"), low = J.N(rec, "low1");
            string hint;
            if (prev == null) hint = "Это первый замер. Примените настройки, перезагрузите ПК и пройдите карту ещё раз — увидите разницу.";
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
            foreach (var w in new[] { 2.2, 1, 1 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
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
            // дата — когда Nolagi забрал результат из консоли, а не когда пройдена карта (в логе CS2 нет времени)
            addRow(0, new[] { "Забрано", "Средний", "1% low" }, "Mut");
            int row = 1;
            foreach (var h in hist.Take(10))
            {
                addRow(row++, new[]
                {
                    When(J.S(h, "at")),
                    J.N(h, "avg").ToString("0", CultureInfo.InvariantCulture),
                    J.N(h, "low1").ToString("0", CultureInfo.InvariantCulture)
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
    }
}
