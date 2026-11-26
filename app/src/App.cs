// Nolagi.exe - window and screens. Layout in code, styles in Theme.xaml.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Pckit
{
    public class App : Application
    {
        public const string Version = "1.4.0";

        [STAThread]
        public static void Main()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("appkey.txt"))
                if (s != null) using (var r = new StreamReader(s)) Telemetry.Key = r.ReadToEnd().Trim();
            var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.ToLowerInvariant()).ToArray();
            if (args.Contains("/uninstall")) { Install.Uninstall(); return; }
            if (Install.EnsureInstalled(args)) return;   // copied to Program Files and started from there

            // одна копия: вторая (ярлык, пока Nolagi в трее) просто показывает окно первой
            bool tray = args.Contains("/tray"), first;
            var single = new System.Threading.Mutex(true, "Local\\Nolagi.App" + (SafeDir.Elevated ? "" : ".dev"), out first);
            if (!first)
            {
                if (!tray) try { System.Threading.EventWaitHandle.OpenExisting(Pckit.MainWindow.ShowEventName).Set(); } catch { }
                return;
            }

            var app = new App();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) =>
            {
                Telemetry.Error("crash", e.Exception.GetType().Name + ": " + e.Exception.Message + "\n" + e.Exception.StackTrace);
                MessageBox.Show("Непредвиденная ошибка:\n" + e.Exception.Message, "Nolagi", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(s);
            app.Run(new MainWindow(tray));
            GC.KeepAlive(single);
        }
    }

    /// One tweak row in «Настройки».
    class ActionItem
    {
        public Dictionary<string, object> Data;
        public CheckBox Check;
        public Border Row;
        public TextBlock Badge;
        public Border BadgeBox;
        public string Id { get { return J.S(Data, "id"); } }
        public string Title { get { return J.S(Data, "title"); } }
        public string Group { get { return J.S(Data, "group"); } }
        public string Status { get { return J.S(Data, "status"); } }
        public bool Applied { get { return Status == "applied"; } }
        public bool Fits { get { return Status != "notfit"; } }
        // only changes Nolagi itself recorded can be rolled back one by one
        public bool Undoable { get { return J.B(Data, "undoable") && (Applied || Status == "partial"); } }
    }

    class GroupBox2
    {
        public string Name;
        public CheckBox Check;
        public TextBlock Count;
        public StackPanel Panel;
        public List<ActionItem> Items = new List<ActionItem>();
    }

    public partial class MainWindow : Window
    {
        Grid root;
        Grid scanScreen, shell, overlay;
        ContentControl page;
        RadioButton navPc, navSet, navClean, navStart, navUndo, navCheck, navApps;
        Settings cfg;

        Dictionary<string, object> hw;
        List<ActionItem> items = new List<ActionItem>();
        List<GroupBox2> groups = new List<GroupBox2>();
        int undoCount;

        public MainWindow(bool hidden)
        {
            Title = "Nolagi";
            Width = 1240; Height = 740; MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 14;
            Background = Res("Bg");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            root = new Grid();
            Content = root;
            cfg = Settings.Load();
            Telemetry.Cfg = cfg;
            InitInstance(hidden);
            Loaded += async (s, e) => { if (startHidden) Hide(); await Start(); };
        }

        static Brush Res(string k) { return (Brush)Application.Current.Resources[k]; }
        static Style St(string k) { return (Style)Application.Current.Resources[k]; }

        static TextBlock T(string text, double size = 14, string color = "Txt", FontWeight? w = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = Res(color), TextWrapping = TextWrapping.Wrap };
            if (w.HasValue) t.FontWeight = w.Value;
            return t;
        }

        static TextBlock Glyph(string glyph, double size = 16, string color = "Txt")
        {
            return new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, Foreground = Res(color), VerticalAlignment = VerticalAlignment.Center };
        }

        static Border Card(UIElement child, Thickness? pad = null)
        {
            return new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = pad ?? new Thickness(18), Child = child };
        }

        static Button Btn(string text, string style = "Btn")
        {
            return new Button { Content = text, Style = St(style) };
        }

        /// Spinning ring: tells the user the program is busy, not frozen.
        static FrameworkElement Spinner(double size = 28)
        {
            var e = new Ellipse
            {
                Width = size, Height = size, Stroke = Res("Acc"), StrokeThickness = size / 9,
                StrokeDashArray = new DoubleCollection { Math.PI * (size - size / 9) / (size / 9) * 0.7, 100 },
                StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rt = new RotateTransform();
            e.RenderTransform = rt;
            rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            return e;
        }

        static void FadeIn(UIElement el, double ms = 250)
        {
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
        }

        // =================================================================== start / scan

        async Task Start()
        {
            ShowScan();
            try
            {
                await Task.Run(() => Payload.Extract());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось распаковать файлы программы:\n" + ex.Message, "Nolagi", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            Dictionary<string, object> res;
            try
            {
                res = await Bridge.Run("scan", null, m => Dispatcher.BeginInvoke(new Action(() => OnScanMsg(m))));
            }
            catch (Exception ex)
            {
                Telemetry.Error("scan", ex.Message);
                MessageBox.Show("Не удалось проверить компьютер:\n" + ex.Message, "Nolagi", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            await FinishScanSteps();
            hw = J.O(res, "hw");
            Telemetry.Hw = hw;
            undoCount = (int)J.N(res, "undoCount");
            BuildItems(J.A(res, "actions"));
            myths = J.A(res, "myths");
            await Task.Delay(400);
            root.Children.Remove(scanScreen);
            BuildShell();
            InitFpsAny();
            InitGameMode();
            if (!neverShown) AskConsentOnce();   // при запуске в трей окно не показывается — спросим в следующий раз
            Telemetry.Send("start", null);
            await CheckUpdates();
        }

        // scan step rows: key -> (spinner holder, check icon, text)
        readonly List<string> scanKeys = new List<string> { "cpu", "gpu", "disk", "cs2", "tweaks" };
        readonly Dictionary<string, Grid> scanMarks = new Dictionary<string, Grid>();
        readonly Dictionary<string, TextBlock> scanTexts = new Dictionary<string, TextBlock>();
        readonly Queue<string> scanQueue = new Queue<string>();
        int scanActive = -1;
        DispatcherTimer scanTimer;
        ProgressBar scanBar;
        TextBlock scanDetail;

        void ShowScan()
        {
            scanScreen = new Grid { Background = Res("Bg") };
            var col = new StackPanel { Width = 420, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var logo = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            logo.Children.Add(LogoMark(40));
            logo.Children.Add(new TextBlock { Text = "  Nolagi", FontSize = 30, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            col.Children.Add(logo);
            col.Children.Add(new TextBlock { Text = "Проверяю ваш компьютер…", FontSize = 16, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) });

            var list = new StackPanel();
            string[] names = { "Процессор и память", "Видеокарта и драйвер", "Диски и сеть", "Steam, CS2 и Dota 2", "Текущие настройки Windows" };
            for (int i = 0; i < scanKeys.Count; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var mark = new Grid { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left };
                mark.Children.Add(new Ellipse { Stroke = Res("Line"), StrokeThickness = 2 });
                var txt = new TextBlock { Text = names[i], FontSize = 15, Foreground = Res("Mut"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(txt, 1);
                row.Children.Add(mark); row.Children.Add(txt);
                list.Children.Add(row);
                scanMarks[scanKeys[i]] = mark; scanTexts[scanKeys[i]] = txt;
            }
            col.Children.Add(Card(list, new Thickness(22, 14, 22, 14)));
            scanBar = new ProgressBar { Style = St("Bar"), Margin = new Thickness(0, 22, 0, 6), IsIndeterminate = false, Maximum = 100 };
            col.Children.Add(scanBar);
            scanDetail = new TextBlock { FontSize = 12, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            col.Children.Add(scanDetail);
            scanScreen.Children.Add(col);
            root.Children.Add(scanScreen);
            FadeIn(col, 400);

            // steps advance with a small minimum delay so each one is visible
            scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            scanTimer.Tick += (s, e) => { if (scanQueue.Count > 0) ActivateScanStep(scanQueue.Dequeue()); };
            scanTimer.Start();
            ActivateScanStep("cpu");
        }

        void ActivateScanStep(string key)
        {
            int idx = scanKeys.IndexOf(key);
            if (idx <= scanActive) return;
            for (int i = Math.Max(scanActive, 0); i < idx; i++) MarkScanDone(scanKeys[i]);
            scanActive = idx;
            var m = scanMarks[key];
            m.Children.Clear();
            m.Children.Add(Spinner(22));
            scanTexts[key].Foreground = Res("Txt");
            scanTexts[key].FontWeight = FontWeights.SemiBold;
            AnimateBar(scanBar, Math.Max(scanBar.Value, idx * 18 + 4));
        }

        void MarkScanDone(string key)
        {
            var m = scanMarks[key];
            m.Children.Clear();
            m.Children.Add(new Ellipse { Fill = Res("Ok") });
            var ic = Glyph("", 12, "Card"); ic.Foreground = Brushes.White; ic.HorizontalAlignment = HorizontalAlignment.Center;
            m.Children.Add(ic);
            FadeIn(m, 200);
            scanTexts[key].FontWeight = FontWeights.Normal;
        }

        void OnScanMsg(Dictionary<string, object> m)
        {
            string t = J.S(m, "t");
            if (t == "step") { if (J.S(m, "key") != "cpu") scanQueue.Enqueue(J.S(m, "key")); }
            else if (t == "progress")
            {
                double i = J.N(m, "i"), n = Math.Max(1, J.N(m, "n"));
                scanDetail.Text = J.S(m, "text");
                if (scanActive == scanKeys.Count - 1) AnimateBar(scanBar, 76 + 22 * i / n);
            }
        }

        async Task FinishScanSteps()
        {
            while (scanQueue.Count > 0) await Task.Delay(120);
            await Task.Delay(300);
            scanTimer.Stop();
            foreach (var k in scanKeys) MarkScanDone(k);
            AnimateBar(scanBar, 100);
            scanDetail.Text = "Готово";
        }

        static void AnimateBar(ProgressBar b, double to)
        {
            b.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(350)) { EasingFunction = new QuadraticEase() });
        }

        // =================================================================== shell

        void BuildShell()
        {
            shell = new Grid();
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            shell.ColumnDefinitions.Add(new ColumnDefinition());

            var nav = new StackPanel { Margin = new Thickness(12, 16, 8, 16) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 18) };
            brand.Children.Add(LogoMark(26));
            brand.Children.Add(new TextBlock { Text = "  Nolagi", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            brand.Children.Add(new TextBlock { Text = "  " + App.Version, FontSize = 12, Foreground = Res("Mut"), VerticalAlignment = VerticalAlignment.Center });
            nav.Children.Add(brand);
            navPc = NavItem("", "Мой ПК", ShowMyPc);
            navSet = NavItem("", "Настройки", ShowSettings);
            navClean = NavItem("", "Чистка", ShowClean);
            navStart = NavItem("", "Автозагрузка", ShowStartup);
            navUndo = NavItem("", "Откат", ShowUndo);
            navCheck = NavItem("", "Проверка", ShowCheck);
            navApps = NavItem("", "Приложения", ShowApps);
            navFps = NavItem("\uE916", "Замер FPS", ShowFps);
            navFeedback = NavItem("", "Написать автору", ShowFeedback);
            navMon = NavItem("", "Мониторинг", ShowMonitor);
            navMon.Unchecked += (s, e) => StopMonitor();
            navGame = NavItem("", "Игровой режим", ShowGameMode);
            navGame.Unchecked += (s, e) => gmStatus = null;
            foreach (var n in new[] { navPc, navSet, navGame, navMon, navFps, navCheck, navClean, navApps, navStart, navUndo }) nav.Children.Add(n);
            nav.Children.Add(new Border { Height = 1, Background = Res("Line"), Margin = new Thickness(12, 10, 12, 10) });
            nav.Children.Add(navFeedback);
            shell.Children.Add(nav);

            var right = new Grid { Margin = new Thickness(8, 16, 16, 16) };
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition());
            bannerHost = new StackPanel();
            right.Children.Add(bannerHost);
            page = new ContentControl();
            Grid.SetRow(page, 1);
            right.Children.Add(page);
            Grid.SetColumn(right, 1);
            shell.Children.Add(right);
            root.Children.Add(shell);
            FadeIn(shell, 300);
            navPc.IsChecked = true;
        }

        /// N with a lightning-bolt diagonal on a dark rounded square.
        public static FrameworkElement LogoMark(double size)
        {
            var c = new Canvas { Width = 120, Height = 120 };
            c.Children.Add(new Rectangle { Width = 120, Height = 120, RadiusX = 28, RadiusY = 28, Fill = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)) });
            c.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(Logo.Data), Stroke = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), StrokeThickness = 9,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
            return new Viewbox { Width = size, Height = size, Child = c, VerticalAlignment = VerticalAlignment.Center };
        }

        // =================================================================== update + announcements

        StackPanel bannerHost;
        RadioButton navFeedback;
        List<Dictionary<string, object>> news = new List<Dictionary<string, object>>();

        async Task CheckUpdates()
        {
            var info = await Telemetry.CheckUpdate();
            if (info == null) return;
            news = J.A(info, "announcements").Where(a => !cfg.SeenNews.Contains((int)J.N(a, "id"))).ToList();
            if (navPc.IsChecked == true && news.Count > 0) ShowMyPc();
            var latest = J.O(info, "latest");
            if (latest != null) ShowUpdateBanner(latest);
        }

        void ShowUpdateBanner(Dictionary<string, object> rel)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = Glyph("", 20, "Acc"); ic.Margin = new Thickness(0, 0, 14, 0);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "Вышла новая версия Nolagi " + J.S(rel, "version"), FontWeight = FontWeights.SemiBold });
            string notes = J.S(rel, "notes");
            if (notes.Length > 0) sp.Children.Add(new TextBlock { Text = notes, FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = notes });
            var btn = Btn("Обновить", "Primary"); btn.VerticalAlignment = VerticalAlignment.Center; btn.Margin = new Thickness(12, 0, 0, 0);
            btn.Click += async (s, e) => await DoUpdate(rel);
            Grid.SetColumn(sp, 1); Grid.SetColumn(btn, 2);
            g.Children.Add(ic); g.Children.Add(sp); g.Children.Add(btn);
            var b = new Border { Background = Res("AccSoft"), BorderBrush = Res("Acc"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 8, 14), Child = g };
            bannerHost.Children.Add(b);
            FadeIn(b, 300);
        }

        async Task DoUpdate(Dictionary<string, object> rel)
        {
            var ov = ShowOverlay("Обновляю Nolagi до " + J.S(rel, "version"));
            ov.Wait.Text = "Скачиваю новую версию с сервера. Программа перезапустится сама.";
            ov.Progress(0, 1, "Скачивание…");
            string tmp;
            try
            {
                tmp = await Updater.Download(J.S(rel, "url"), J.S(rel, "sha256"),
                    p => Dispatcher.BeginInvoke(new Action(() => { ov.Step.Text = "Скачивание… " + p + "%"; ov.Counter.Text = ""; AnimateBar(ov.Bar, p); })));
            }
            catch (Exception ex)
            {
                Telemetry.Error("update", ex.Message);
                ov.Done("Не удалось обновить", new List<Tuple<bool, string, string>> { Tuple.Create(false, "Скачивание", ex.Message) }, "Попробуйте позже или скачайте новую версию у автора.");
                return;
            }
            ov.Step.Text = "Перезапускаю…";
            AnimateBar(ov.Bar, 100);
            await Task.Delay(600);
            try { Updater.InstallAndRestart(tmp, J.S(rel, "sha256")); }
            catch (Exception ex)
            {
                ov.Done("Не удалось обновить", new List<Tuple<bool, string, string>> { Tuple.Create(false, "Установка", ex.Message) }, null);
                return;
            }
            currentOv.Finished = true;
            Application.Current.Shutdown();
        }

        UIElement NewsCards()
        {
            var sp = new StackPanel();
            foreach (var a in news.ToList())
            {
                bool warn = J.S(a, "level") == "warn";
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var ic = Glyph(warn ? "" : "", 18, warn ? "Warn" : "Acc"); ic.Margin = new Thickness(0, 0, 12, 0); ic.VerticalAlignment = VerticalAlignment.Top;
                var tx = new StackPanel();
                tx.Children.Add(new TextBlock { Text = "Сообщение от автора", FontSize = 12, Foreground = Res(warn ? "Warn" : "Acc"), FontWeight = FontWeights.SemiBold });
                tx.Children.Add(new TextBlock { Text = J.S(a, "text"), TextWrapping = TextWrapping.Wrap });
                var close = new Button { Content = "Понятно", Style = St("Link"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
                var item = a;
                Grid.SetColumn(tx, 1); Grid.SetColumn(close, 2);
                g.Children.Add(ic); g.Children.Add(tx); g.Children.Add(close);
                var card = new Border { Background = Res(warn ? "WarnSoft" : "AccSoft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 12, 12), MaxWidth = 868, HorizontalAlignment = HorizontalAlignment.Left, Child = g };
                close.Click += (s, e) =>
                {
                    cfg.SeenNews.Add((int)J.N(item, "id")); cfg.Save();
                    news.Remove(item);
                    sp.Children.Remove(card);
                };
                sp.Children.Add(card);
            }
            return sp;
        }

        // =================================================================== Написать автору

        void ShowFeedback()
        {
            var sp = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            var box = new TextBox { Style = St("Search"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxLength = 4000, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var hint = new TextBlock { Text = "Что понравилось, что сломалось, какую настройку добавить…", Foreground = Res("Mut"), IsHitTestVisible = false, Margin = new Thickness(12, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            box.TextChanged += (s, e) => hint.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            var area = new Grid();
            area.Children.Add(box); area.Children.Add(hint);
            var c = new StackPanel();
            c.Children.Add(area);
            var status = new TextBlock { Foreground = Res("Mut"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
            var send = Btn("Отправить", "Primary"); send.HorizontalAlignment = HorizontalAlignment.Left; send.Margin = new Thickness(0, 12, 0, 0);
            send.Click += async (s, e) =>
            {
                string text = (box.Text ?? "").Trim();
                if (text.Length < 3) { status.Text = "Напишите хотя бы пару слов."; status.Foreground = Res("Bad"); return; }
                send.IsEnabled = false; status.Foreground = Res("Mut"); status.Text = "Отправляю…";
                try
                {
                    await Telemetry.Feedback(text);
                    box.Text = ""; status.Foreground = Res("Ok"); status.Text = "✓ Отправлено. Спасибо! Автор прочитает сообщение в своей панели.";
                }
                catch { status.Foreground = Res("Bad"); status.Text = "Не получилось отправить — проверьте интернет и попробуйте ещё раз."; }
                send.IsEnabled = true;
            };
            c.Children.Add(send);
            c.Children.Add(status);
            sp.Children.Add(Card(c));
            sp.Children.Add(new TextBlock { Text = "Вместе с сообщением отправляются ваш ник (" + (string.IsNullOrEmpty(cfg.Nick) ? "без ника" : cfg.Nick) + ") и версия программы.", FontSize = 12.5, Foreground = Res("Mut"), Margin = new Thickness(4, 10, 0, 0), TextWrapping = TextWrapping.Wrap });

            // consent can be changed at any time, not only in the first-run dialog
            var stats = new CheckBox { Style = St("Chk"), IsChecked = cfg.Consent, Margin = new Thickness(4, 18, 0, 0) };
            stats.Content = new TextBlock { Text = "Отправлять автору статистику: ник, процессор, видеокарту, объём памяти, версию Windows и какие настройки применились. Подробно — в PRIVACY.md на странице программы.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), FontSize = 13 };
            stats.Click += (s, e) => { cfg.Consent = stats.IsChecked == true; cfg.Save(); };
            sp.Children.Add(stats);
            SetPage(PageFrame("Написать автору", "Сообщение придёт автору Nolagi.", sp));
        }

        RadioButton NavItem(string glyph, string text, Action show)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Glyph(glyph, 16));
            sp.Children.Add(new TextBlock { Text = "   " + text, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Content = sp, Style = St("Nav"), GroupName = "nav" };
            rb.Checked += (s, e) => show();
            return rb;
        }

        void SetPage(UIElement content)
        {
            page.Content = content;
            FadeIn(content, 180);
        }

        static Grid PageFrame(string title, string subtitle, UIElement body, UIElement bottom = null)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel { Margin = new Thickness(4, 0, 0, 14) };
            head.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(subtitle)) head.Children.Add(new TextBlock { Text = subtitle, FontSize = 14, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(head);
            Grid.SetRow((UIElement)body, 1); g.Children.Add(body);
            if (bottom != null) { Grid.SetRow(bottom, 2); g.Children.Add(bottom); }
            return g;
        }

        // =================================================================== Мой ПК

        void ShowMyPc()
        {
            var wrap = new WrapPanel();
            Func<string, string, string, string, Border> tile = (glyph, cap, main, sub) =>
            {
                var sp = new StackPanel();
                var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                top.Children.Add(Glyph(glyph, 16, "Acc"));
                top.Children.Add(new TextBlock { Text = "  " + cap, Foreground = Res("Mut"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(top);
                sp.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(main) ? "—" : main, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrEmpty(sub)) sp.Children.Add(new TextBlock { Text = sub, FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                var c = Card(sp);
                c.Width = 280; c.MinHeight = 108; c.Margin = new Thickness(0, 0, 12, 12);
                return c;
            };
            string ram = J.N(hw, "ramGb") > 0 ? J.N(hw, "ramGb") + " ГБ" : "";
            string ramSub = (J.S(hw, "ramType") + (J.N(hw, "ramMhz") > 0 ? " · " + J.N(hw, "ramMhz") + " МГц" : "")).Trim(' ', '·');
            wrap.Children.Add(tile("", "Процессор", Clean(J.S(hw, "cpu")), null));
            wrap.Children.Add(tile("", "Видеокарта", Clean(J.S(hw, "gpu")), J.S(hw, "driver").Length > 0 ? "Драйвер " + J.S(hw, "driver") : null));
            wrap.Children.Add(tile("", "Оперативная память", ram, ramSub));
            object ssd = J.Get(hw, "ssd");
            wrap.Children.Add(tile("", "Системный диск", ssd is bool ? ((bool)ssd ? "SSD" : "HDD (медленный)") : "", null));
            wrap.Children.Add(tile("", "Монитор", J.N(hw, "hz") > 0 ? J.N(hw, "hz") + " Гц" : "", null));
            wrap.Children.Add(tile("", "Windows", J.S(hw, "os"), J.S(hw, "build").Length > 0 ? "сборка " + J.S(hw, "build") : null));
            wrap.Children.Add(tile("", "Сеть", J.B(hw, "wired") ? "Кабель" : (J.B(hw, "wifi") ? "Только Wi‑Fi" : "—"), J.B(hw, "wifi") ? "Для игр лучше кабель: пинг стабильнее" : null));
            wrap.Children.Add(tile("", "CS2", J.B(hw, "cs2") ? "Найдена" : "Не найдена", null));
            wrap.Children.Add(tile("", "Dota 2", J.B(hw, "dota") ? "Найдена" : "Не найдена", null));
            if (J.B(hw, "laptop")) wrap.Children.Add(tile("", "Тип", "Ноутбук", "Играйте от сети"));

            int fit = items.Count(i => i.Fits);
            int on = items.Count(i => i.Fits && i.Applied);
            var st = new StackPanel();
            st.Children.Add(new TextBlock { Text = "Состояние настроек", FontSize = 13, Foreground = Res("Mut"), Margin = new Thickness(0, 0, 0, 6) });
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = on.ToString(), FontSize = 30, FontWeight = FontWeights.SemiBold });
            line.Children.Add(new TextBlock { Text = "  из " + fit + " настроек включено", FontSize = 15, Foreground = Res("Mut"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) });
            st.Children.Add(line);
            var bar = new ProgressBar { Style = St("Bar"), Maximum = Math.Max(1, fit), Value = 0, Margin = new Thickness(0, 10, 0, 8) };
            st.Children.Add(bar);
            AnimateBar(bar, on);
            st.Children.Add(new TextBlock { Text = undoCount > 0 ? "Изменений Nolagi, которые можно откатить: " + undoCount : "Nolagi ещё ничего не менял на этом ПК", FontSize = 13, Foreground = Res("Mut") });
            var stCard = Card(st);
            stCard.Margin = new Thickness(0, 0, 12, 12);
            stCard.MaxWidth = 868;
            stCard.HorizontalAlignment = HorizontalAlignment.Left;

            var body = new StackPanel();
            if (news.Count > 0) body.Children.Add(NewsCards());
            body.Children.Add(stCard);
            body.Children.Add(wrap);
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
            SetPage(PageFrame("Мой ПК", "Что установлено в компьютере. Менять настройки — во вкладке «Настройки».", sv));
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace("(R)", "").Replace("(TM)", "").Replace(" 6-Core Processor", "").Replace(" 8-Core Processor", "").Replace("NVIDIA GeForce ", "GeForce ").Trim();
        }

        // =================================================================== Настройки

        TextBox search;
        TextBlock selInfo;
        string selAllNote = "";   // what «Выбрать всё» left out and why
        Button applyBtn, undoSelBtn, clearBtn;
        Grid settingsPage;
        StackPanel settingsList;
        ScrollViewer settingsScroll;

        // big sections on top of «Настройки»; small groups from actions.json are mapped into them
        static readonly string[] Sections = { "Игры", "Видеокарта", "Производительность", "Система", "Приватность и ИИ", "На свой риск" };
        const string RiskSection = "На свой риск";
        string curSection = "";                       // "" = all
        Dictionary<string, Button> sectionTabs = new Dictionary<string, Button>();
        List<Dictionary<string, object>> myths = new List<Dictionary<string, object>>();
        UIElement mythPanel;
        UIElement prioPanel;
        StackPanel prioBody;

        // generic exe names: IFEO applies to every program with this file name
        static readonly string[] GenericExe = { "game.exe", "launcher.exe", "start.exe", "play.exe", "client.exe", "app.exe", "main.exe", "run.exe", "setup.exe", "win64.exe" };

        async void LoadPriorityPanel()
        {
            var body = prioBody;
            if (body == null) return;
            body.Children.Clear();
            var w = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            w.Children.Add(Spinner(18));
            w.Children.Add(new TextBlock { Text = "  Ищу игры в Steam…", Foreground = Res("Mut"), VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(w);
            Dictionary<string, object> r;
            try { r = await Bridge.Run("prio-list", null, null); }
            catch (Exception ex)
            {
                body.Children.Clear();
                body.Children.Add(new TextBlock { Text = "Не удалось получить список: " + ex.Message, Foreground = Res("Bad"), TextWrapping = TextWrapping.Wrap });
                return;
            }
            if (body != prioBody) return;
            body.Children.Clear();
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs2.exe", "dota2.exe" };   // have their own settings
            var high = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arr = J.Get(r, "high") as object[];
            if (arr != null) foreach (var o in arr) high.Add(Convert.ToString(o));
            var rows = new List<Tuple<string, string, bool>>();   // name, exe, high
            foreach (var g in J.A(r, "games"))
                if (!own.Contains(J.S(g, "exe"))) rows.Add(Tuple.Create(J.S(g, "name"), J.S(g, "exe"), high.Contains(J.S(g, "exe"))));
            foreach (var exe in high)
                if (!own.Contains(exe) && !rows.Any(x => string.Equals(x.Item2, exe, StringComparison.OrdinalIgnoreCase))) rows.Add(Tuple.Create(exe, exe, true));

            if (rows.Count == 0)
                body.Children.Add(new TextBlock { Text = "Других игр в Steam не найдено. Можно выбрать exe игры вручную.", Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
            bool first = true;
            foreach (var row in rows.OrderBy(x => x.Item3 ? 0 : 1).ThenBy(x => x.Item1))
            {
                var g = new Grid { Margin = new Thickness(0, first ? 0 : 6, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var tl = new WrapPanel();
                tl.Children.Add(new TextBlock { Text = row.Item1, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                if (row.Item3) tl.Children.Add(Pill("высокий приоритет", "OkSoft", "Ok", null));
                t.Children.Add(tl);
                if (row.Item1 != row.Item2) t.Children.Add(new TextBlock { Text = row.Item2, FontSize = 12.5, Foreground = Res("Mut") });
                var b = Btn(row.Item3 ? "Убрать" : "Включить", row.Item3 ? "Btn" : "Primary");
                b.VerticalAlignment = VerticalAlignment.Center;
                string exe = row.Item2; bool on = row.Item3;
                b.Click += async (s, e) => await SetGamePriority(exe, !on);
                Grid.SetColumn(b, 1);
                g.Children.Add(t); g.Children.Add(b);
                body.Children.Add(g);
                first = false;
            }
            var pick = new Button { Content = "Выбрать exe игры вручную…", Style = St("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            pick.Click += async (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программа (*.exe)|*.exe", Title = "Выберите exe игры" };
                if (dlg.ShowDialog(this) != true) return;
                string name = System.IO.Path.GetFileName(dlg.FileName);
                if (GenericExe.Contains(name.ToLowerInvariant()) &&
                    MessageBox.Show(this, "У файла слишком общее имя «" + name + "». Высокий приоритет получат все программы с таким именем.\n\nПродолжить?", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await SetGamePriority(name, true);
            };
            body.Children.Add(pick);
        }

        async Task SetGamePriority(string exe, bool high)
        {
            var ov = ShowOverlay(high ? "Включаю высокий приоритет" : "Убираю высокий приоритет");
            ov.Step.Text = exe; ov.Counter.Text = "";
            var lines = new List<Tuple<bool, string, string>>();
            string head;
            try
            {
                var r = await RunTool("", high ? "prio-set" : "prio-unset", new List<string> { exe }, lines, ov);
                undoCount = (int)J.N(r, "undoCount");
                head = J.N(r, "failed") > 0 ? "Не получилось" : (high ? "Готово: " + exe + " — высокий приоритет" : "Готово: приоритет " + exe + " обычный");
            }
            catch (Exception ex) { head = "Ошибка: " + ex.Message; }
            ov.Done(head, lines, high ? "Действует со следующего запуска игры. Отменить — здесь же или во вкладке «Откат»." : "Действует со следующего запуска игры.");
            LoadPriorityPanel();
        }

        static string SectionOf(string group)
        {
            switch (group)
            {
                case "CS2": case "Dota 2": case "Игровой режим": case "Ввод": return "Игры";
                case "Видеокарта": case "Графика": case "Работа": return "Видеокарта";
                case "Питание": case "Планировщик": case "Сеть": case "Исправления": return "Производительность";
                case "Конфиденциальность": case "ИИ": return "Приватность и ИИ";
                case "На свой риск": return RiskSection;
                default: return "Система";            // Фон, Службы и задачи, Обновления, Удобство and anything new
            }
        }

        void BuildItems(List<Dictionary<string, object>> rows)
        {
            items.Clear(); groups.Clear();
            foreach (var r in rows)
            {
                var it = new ActionItem { Data = r };
                items.Add(it);
                var g = groups.FirstOrDefault(x => x.Name == it.Group);
                if (g == null) { g = new GroupBox2 { Name = it.Group }; groups.Add(g); }
                g.Items.Add(it);
            }
            // keep file order inside a section, sections in the fixed order
            var ordered = groups.Select((g, i) => new { g, i }).OrderBy(x => Array.IndexOf(Sections, SectionOf(x.g.Name))).ThenBy(x => x.i).Select(x => x.g).ToList();
            groups.Clear(); groups.AddRange(ordered);
            settingsPage = null;
        }

        void ShowSettings()
        {
            if (settingsPage == null) settingsPage = BuildSettingsPage();
            RefreshSelection();
            SetPage(settingsPage);
        }

        Grid BuildSettingsPage()
        {
            settingsList = new StackPanel();
            foreach (var g in groups)
            {
                var gp = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                var head = new Grid { Margin = new Thickness(4, 0, 0, 8) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition());
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var gc = new CheckBox { Style = St("Chk"), IsThreeState = false, ToolTip = "Выбрать всё в этой категории" };
                var gname = new TextBlock { Text = g.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var cnt = new TextBlock { FontSize = 13, Foreground = Res("Mut"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(gname, 1); Grid.SetColumn(cnt, 2);
                head.Children.Add(gc); head.Children.Add(gname); head.Children.Add(cnt);
                var grp = g;
                gc.Click += (s, e) =>
                {
                    // partly selected -> select all; all selected -> clear (WPF itself would turn «partly» into «none»)
                    var vis = VisibleEnabled(grp);
                    bool on = vis.Any(it => it.Check.IsChecked != true);
                    foreach (var it in vis) it.Check.IsChecked = on;
                    RefreshSelection();
                };
                g.Check = gc; g.Count = cnt;
                gp.Children.Add(head);

                var cardList = new StackPanel();
                for (int i = 0; i < g.Items.Count; i++)
                {
                    var row = BuildRow(g.Items[i]);
                    if (i > 0) row.BorderThickness = new Thickness(0, 1, 0, 0);
                    cardList.Children.Add(row);
                }
                var card = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = cardList, ClipToBounds = true };
                gp.Children.Add(card);
                g.Panel = gp;
                settingsList.Children.Add(gp);
            }

            // myths: read-only cards at the end of «На свой риск»
            if (myths.Count > 0)
            {
                var mp = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                var mh = new StackPanel { Margin = new Thickness(4, 0, 0, 8) };
                mh.Children.Add(new TextBlock { Text = "Мифы", FontSize = 18, FontWeight = FontWeights.SemiBold });
                mh.Children.Add(new TextBlock { Text = "Популярные «ускорения», которые не работают или вредят. Nolagi их не включает — здесь только объяснение почему.", FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                mp.Children.Add(mh);
                var ml = new StackPanel();
                for (int i = 0; i < myths.Count; i++)
                {
                    var m = myths[i];
                    var t = new StackPanel();
                    var tl = new WrapPanel();
                    tl.Children.Add(Pill("миф", "Bg", "Mut", null));
                    tl.Children.Add(new TextBlock { Text = J.S(m, "title"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                    t.Children.Add(tl);
                    if (J.S(m, "what").Length > 0) t.Children.Add(new TextBlock { Text = J.S(m, "what"), FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                    if (J.S(m, "effect").Length > 0) t.Children.Add(new TextBlock { Text = "На деле: " + J.S(m, "effect"), FontSize = 12.5, Foreground = Res("Txt"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                    ml.Children.Add(new Border { Padding = new Thickness(16, 11, 16, 11), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0), Child = t });
                }
                mp.Children.Add(new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = ml });
                mythPanel = mp;
                settingsList.Children.Add(mp);
            }
            else mythPanel = null;

            // «Высокий приоритет для других игр» — right after the last group of «Игры»
            var pp = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var ph = new StackPanel { Margin = new Thickness(4, 0, 0, 8) };
            ph.Children.Add(new TextBlock { Text = "Высокий приоритет для других игр", FontSize = 18, FontWeight = FontWeights.SemiBold });
            ph.Children.Add(new TextBlock { Text = "Как в Process Lasso, но без программы в фоне: Windows сама даёт игре высокий приоритет процессора при каждом запуске. Фон реже отнимает процессор — меньше редких просадок. CS2 и Dota 2 настраиваются выше. Всё откатывается.", FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
            pp.Children.Add(ph);
            prioBody = new StackPanel();
            pp.Children.Add(new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 16, 12), Child = prioBody });
            prioPanel = pp;
            int lastGames = -1;
            for (int i = 0; i < groups.Count; i++) if (SectionOf(groups[i].Name) == "Игры") lastGames = settingsList.Children.IndexOf(groups[i].Panel);
            if (lastGames >= 0) settingsList.Children.Insert(lastGames + 1, pp);
            else settingsList.Children.Insert(0, pp);
            LoadPriorityPanel();

            // section tabs: «Все» + one per section that has settings
            sectionTabs.Clear();
            var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var sec in new[] { "" }.Concat(Sections))
            {
                string s0 = sec;
                if (s0.Length > 0 && !groups.Any(g => SectionOf(g.Name) == s0)) continue;
                var b = Btn(s0.Length == 0 ? "Все" : s0);
                b.Margin = new Thickness(0, 0, 4, 6); b.Padding = new Thickness(9, 4, 9, 4); b.FontSize = 12.5;
                b.Click += (s, e) => { curSection = s0; search.Text = ""; ApplyFilter(); };
                sectionTabs[s0] = b;
                tabs.Children.Add(b);
            }
            UpdateGroupCounts();

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = settingsList, Padding = new Thickness(0, 0, 8, 0) };
            settingsScroll = sv;

            // top bar: search + select all
            var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            search = new TextBox { Style = St("Search"), ToolTip = "Поиск по названию" };
            var hint = new TextBlock { Text = "Поиск настройки…", Foreground = Res("Mut"), IsHitTestVisible = false, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            search.TextChanged += (s, e) => { hint.Visibility = search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; ApplyFilter(); };
            top.Children.Add(search); top.Children.Add(hint);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var selAll = new Button { Content = "Выбрать всё", Style = St("Link"), Margin = new Thickness(0, 0, 18, 0) };
            selAll.Click += (s, e) =>
            {
                int risky = 0, notFit = 0, done = 0;
                foreach (var it in items)
                {
                    if (it.Row.Visibility != Visibility.Visible) continue;
                    if (!it.Check.IsEnabled) notFit++;
                    else if (it.Applied) done++;
                    else if (IsRisky(it)) risky++;
                    else it.Check.IsChecked = true;
                }
                var parts = new List<string>();
                if (done > 0) parts.Add(done + " уже включено");
                if (risky > 0) parts.Add(risky + " «на свой риск» — отметьте вручную");
                if (notFit > 0) parts.Add(notFit + " не подходят этому ПК");
                selAllNote = parts.Count > 0 ? "Не отмечено: " + string.Join(", ", parts) : "";
                RefreshSelection();
            };
            selAll.ToolTip = "Отмечает все невключённые настройки. Не отмечает «на свой риск» (их выбирайте сами) и те, что не подходят этому ПК";
            var selRec = new Button { Content = "Выбрать подходящие мне ★", Style = St("Link") };
            selRec.ToolTip = "Только то, что Nolagi советует для вашего железа";
            selRec.Click += (s, e) => { foreach (var it in items) if (it.Check.IsEnabled && !it.Applied && J.B(it.Data, "recommended") && !IsRisky(it)) it.Check.IsChecked = true; RefreshSelection(); };
            tools.Children.Add(selAll); tools.Children.Add(selRec);
            Grid.SetColumn(tools, 2);
            top.Children.Add(tools);

            // bottom bar: selection summary + actions
            var bottom = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(0, 12, 8, 0) };
            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition());
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            clearBtn = Btn("Снять выбор");
            clearBtn.Click += (s, e) => { foreach (var it in items) it.Check.IsChecked = false; RefreshSelection(); };
            undoSelBtn = Btn("Откатить", "Danger");
            undoSelBtn.Margin = new Thickness(8, 0, 0, 0);
            undoSelBtn.Click += async (s, e) => await UndoSelected();
            applyBtn = Btn("Применить", "Primary");
            applyBtn.Margin = new Thickness(8, 0, 0, 0);
            applyBtn.Click += async (s, e) => await ApplySelected();
            btns.Children.Add(clearBtn); btns.Children.Add(undoSelBtn); btns.Children.Add(applyBtn);
            Grid.SetColumn(btns, 1);
            bg.Children.Add(selInfo); bg.Children.Add(btns);
            bottom.Child = bg;

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            body.Children.Add(top);
            Grid.SetRow(tabs, 1); body.Children.Add(tabs);
            Grid.SetRow(sv, 2); body.Children.Add(sv);
            ApplyFilter();
            return PageFrame("Настройки", "Отметьте галочками то, что хотите включить, и нажмите «Применить». Зелёная метка — настройка уже включена.", body, bottom);
        }

        static bool IsRisky(ActionItem it)
        {
            return J.S(it.Data, "danger").Length > 0 || J.B(it.Data, "expert") || J.S(it.Data, "risk") == "high";
        }

        Border BuildRow(ActionItem it)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chk = new CheckBox { Style = St("Chk"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            chk.Checked += (s, e) => RefreshSelection();
            chk.Unchecked += (s, e) => RefreshSelection();
            it.Check = chk;

            var text = new StackPanel();
            var titleLine = new WrapPanel();
            titleLine.Children.Add(new TextBlock { Text = it.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap });
            if (J.B(it.Data, "recommended") && it.Fits) titleLine.Children.Add(Pill("★ подходит вам", "AccSoft", "Acc", J.S(it.Data, "reason")));
            if (IsRisky(it)) titleLine.Children.Add(Pill("на свой риск", "WarnSoft", "Warn", J.S(it.Data, "danger")));
            text.Children.Add(titleLine);
            string what = J.S(it.Data, "what");
            if (what.Length > 0) text.Children.Add(new TextBlock { Text = what, Foreground = Res("Mut"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            string eff = J.S(it.Data, "effect");
            if (eff.Length > 0) text.Children.Add(new TextBlock { Text = "Что даст: " + eff, Foreground = Res("Mut"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Opacity = 0.9 });
            if (!it.Fits)
            {
                chk.IsEnabled = false;
                text.Children.Add(new TextBlock { Text = "Не подходит этому ПК: " + J.S(it.Data, "notFit"), Foreground = Res("Warn"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                text.Opacity = 0.6;
            }
            Grid.SetColumn(text, 1);

            var badge = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
            var bb = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Child = badge, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            it.Badge = badge; it.BadgeBox = bb;
            Grid.SetColumn(bb, 2);
            g.Children.Add(chk); g.Children.Add(text); g.Children.Add(bb);

            var row = new Border { Padding = new Thickness(16, 12, 16, 12), BorderBrush = Res("Line"), Child = g, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (!chk.IsEnabled || e.OriginalSource is CheckBox) return;
                var src = e.OriginalSource as DependencyObject;
                while (src != null && src != row) { if (src == chk) return; src = VisualTreeHelper.GetParent(src); }
                chk.IsChecked = chk.IsChecked != true;
                RefreshSelection();
            };
            it.Row = row;
            UpdateBadge(it);
            return row;
        }

        static Border Pill(string text, string bg, string fg, string tip)
        {
            var b = new Border { Background = Res(bg), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            b.Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Res(fg) };
            if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
            return b;
        }

        void UpdateBadge(ActionItem it)
        {
            string st = it.Status;
            it.BadgeBox.ToolTip = (st == "applied" || st == "partial") && !it.Undoable
                ? "Включено не через Nolagi (вручную или другой программой) — откатить здесь нельзя" : null;
            if (st == "applied") { it.Badge.Text = "✓ Включено"; it.Badge.Foreground = Res("Ok"); it.BadgeBox.Background = Res("OkSoft"); }
            else if (st == "partial") { it.Badge.Text = "Частично"; it.Badge.Foreground = Res("Warn"); it.BadgeBox.Background = Res("WarnSoft"); }
            else if (st == "notfit") { it.Badge.Text = "Не подходит"; it.Badge.Foreground = Res("Mut"); it.BadgeBox.Background = Res("Bg"); }
            else { it.Badge.Text = "Выключено"; it.Badge.Foreground = Res("Mut"); it.BadgeBox.Background = Res("Bg"); }
        }

        void UpdateGroupCounts()
        {
            foreach (var g in groups)
            {
                int fit = g.Items.Count(i => i.Fits);
                g.Count.Text = "включено " + g.Items.Count(i => i.Fits && i.Applied) + " из " + fit;
            }
            foreach (var kv in sectionTabs)
            {
                if (kv.Key.Length == 0) continue;
                var all = groups.Where(g => SectionOf(g.Name) == kv.Key).SelectMany(g => g.Items).Where(i => i.Fits).ToList();
                kv.Value.Content = kv.Key + " " + all.Count(i => i.Applied) + "/" + all.Count;
                kv.Value.ToolTip = "Включено " + all.Count(i => i.Applied) + " из " + all.Count + " подходящих этому ПК";
            }
        }

        void ApplyFilter()
        {
            string q = (search.Text ?? "").Trim().ToLowerInvariant();
            // search looks through all sections; otherwise only the chosen section is shown
            foreach (var kv in sectionTabs) kv.Value.Style = St(q.Length == 0 && kv.Key == curSection ? "Primary" : "Btn");
            if (mythPanel != null) mythPanel.Visibility = q.Length == 0 && (curSection == "" || curSection == RiskSection) ? Visibility.Visible : Visibility.Collapsed;
            if (prioPanel != null) prioPanel.Visibility = (q.Length == 0 && (curSection == "" || curSection == "Игры")) || (q.Length > 0 && ("приоритет игр process lasso".Contains(q))) ? Visibility.Visible : Visibility.Collapsed;
            if (settingsScroll != null) settingsScroll.ScrollToTop();
            foreach (var g in groups)
            {
                bool inSection = q.Length > 0 || curSection == "" || SectionOf(g.Name) == curSection;
                int vis = 0;
                foreach (var it in g.Items)
                {
                    bool show = inSection && (q.Length == 0 || it.Title.ToLowerInvariant().Contains(q) || J.S(it.Data, "what").ToLowerInvariant().Contains(q) || g.Name.ToLowerInvariant().Contains(q));
                    it.Row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show) vis++;
                }
                g.Panel.Visibility = vis > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            RefreshSelection();
        }

        void RefreshSelection()
        {
            if (applyBtn == null) return;
            var sel = items.Where(i => i.Check.IsChecked == true).ToList();
            int toApply = sel.Count(i => !i.Applied);
            int toUndo = sel.Count(i => i.Undoable);
            applyBtn.Content = toApply > 0 ? "Применить (" + toApply + ")" : "Применить";
            applyBtn.IsEnabled = toApply > 0;
            undoSelBtn.Content = toUndo > 0 ? "Откатить (" + toUndo + ")" : "Откатить";
            undoSelBtn.IsEnabled = toUndo > 0;
            undoSelBtn.ToolTip = "Вернуть как было то, что включил Nolagi. Настройки, включённые вручную или другой программой, здесь не откатываются.";
            clearBtn.IsEnabled = sel.Count > 0;
            if (sel.Count == 0) selAllNote = "";
            selInfo.Text = sel.Count == 0 ? "Ничего не выбрано" : "Выбрано: " + sel.Count + (toApply > 0 ? " · включить " + toApply : "") + (toUndo > 0 ? " · откатить " + toUndo : "")
                + (selAllNote.Length > 0 ? "\n" + selAllNote : "");
            foreach (var g in groups)
            {
                var en = VisibleEnabled(g);
                int c = en.Count(i => i.Check.IsChecked == true);
                g.Check.IsChecked = c == 0 ? (bool?)false : (c == en.Count ? (bool?)true : null);
                g.Check.IsEnabled = en.Count > 0;
            }
        }

        static List<ActionItem> VisibleEnabled(GroupBox2 g)
        {
            return g.Items.Where(i => i.Check.IsEnabled && i.Row.Visibility == Visibility.Visible).ToList();
        }

        async Task ApplySelected()
        {
            var sel = items.Where(i => i.Check.IsChecked == true && !i.Applied).ToList();
            if (sel.Count == 0) return;
            var risky = sel.Where(IsRisky).ToList();
            if (risky.Count > 0)
            {
                string list = string.Join("\n", risky.Select(r => "• " + r.Title));
                if (MessageBox.Show(this, "Среди выбранного есть настройки «на свой риск»:\n\n" + list + "\n\nИх всегда можно откатить. Продолжить?", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            await RunWithOverlay("Применяю настройки", "apply", sel.Select(i => i.Id).ToList());
        }

        async Task UndoSelected()
        {
            var sel = items.Where(i => i.Check.IsChecked == true && i.Undoable).ToList();
            if (sel.Count == 0) return;
            await RunWithOverlay("Откатываю настройки", "undo", sel.Select(i => i.Id).ToList());
        }

        /// Runs apply/undo with a modal progress panel and shows a per-item result list.
        async Task RunWithOverlay(string title, string cmd, List<string> ids)
        {
            var titles = items.ToDictionary(i => i.Id, i => i.Title);
            var ov = ShowOverlay(title);
            var results = new List<KeyValuePair<string, bool>>();
            var msgs = new Dictionary<string, string>();
            Dictionary<string, object> res = null;
            string fatal = null;
            try
            {
                res = await Bridge.Run(cmd, ids, m => Dispatcher.BeginInvoke(new Action(() =>
                {
                    string t = J.S(m, "t");
                    if (t == "progress") ov.Progress(J.N(m, "i"), J.N(m, "n"), J.S(m, "text"));
                    else if (t == "item") { results.Add(new KeyValuePair<string, bool>(J.S(m, "id"), J.B(m, "ok"))); msgs[J.S(m, "id")] = J.S(m, "msg"); }
                })));
            }
            catch (Exception ex) { fatal = ex.Message; }

            if (res != null)
            {
                undoCount = (int)J.N(res, "undoCount");
                foreach (var row in J.A(res, "actions"))
                {
                    var it = items.FirstOrDefault(i => i.Id == J.S(row, "id"));
                    if (it == null) continue;
                    it.Data = row;
                    UpdateBadge(it);
                    it.Check.IsChecked = false;
                }
                UpdateGroupCounts();
                RefreshSelection();
            }
            // what was asked vs what is now really on (state re-read from Windows)
            var lines = new List<Tuple<bool, string, string>>();
            foreach (var id in ids)
            {
                var it = items.FirstOrDefault(i => i.Id == id);
                string name = titles.ContainsKey(id) ? titles[id] : id;
                bool ok = results.Any(r => r.Key == id && r.Value);
                string msg = msgs.ContainsKey(id) ? msgs[id] : "";
                if (cmd == "apply" && it != null && ok && !it.Applied) { ok = false; msg = "Windows не принял изменение — проверьте после перезагрузки"; }
                if (cmd == "undo" && it != null && ok && it.Applied) { ok = false; msg = "осталось включённым"; }
                lines.Add(Tuple.Create(ok, name, msg));
            }
            // stats first, then one combined error report for the whole run
            if (cmd == "apply") Telemetry.Send("apply", results);
            else if (cmd == "undo") Telemetry.Send("rollback", results);
            var errs = lines.Where(x => !x.Item1 && x.Item3.Length > 0).Select(x => x.Item2 + ": " + x.Item3).ToList();
            if (fatal != null) errs.Insert(0, fatal);
            if (errs.Count > 0) Telemetry.Error(cmd, string.Join("\n", errs));

            string head = fatal != null ? "Ошибка: " + fatal
                : (cmd == "apply" ? "Применено " + lines.Count(l => l.Item1) + " из " + lines.Count : "Откачено " + lines.Count(l => l.Item1) + " из " + lines.Count);
            string note = cmd == "apply" ? "Часть настроек (сеть, видеокарта, службы) полностью заработает после перезагрузки компьютера." : null;
            if (cmd == "apply" && res != null && J.S(res, "restorePoint") == "failed") note = "Точку восстановления Windows создать не удалось, но всё можно вернуть во вкладке «Откат». " + note;
            ov.Done(head, lines, note);
        }

        // =================================================================== overlay

        class Overlay
        {
            public bool Finished;
            public TextBlock Title, Step, Counter, Wait;
            public ProgressBar Bar;
            public StackPanel Body;
            public FrameworkElement Spin;
            public MainWindow Win;

            public void Progress(double i, double n, string text)
            {
                n = Math.Max(n, 1);
                Step.Text = text;
                Counter.Text = Math.Min(i + 1, n) + " из " + n;
                AnimateBar(Bar, 100.0 * i / n);
            }

            public void Done(string head, List<Tuple<bool, string, string>> lines, string note)
            {
                Finished = true;
                Spin.Visibility = Visibility.Collapsed;
                Wait.Visibility = Visibility.Collapsed;
                Title.Margin = new Thickness(0);
                Title.Text = head;
                AnimateBar(Bar, 100);
                Counter.Text = "";
                Step.Text = "Готово";
                var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
                foreach (var l in lines)
                {
                    var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    var ic = Glyph(l.Item1 ? "" : "", 14, l.Item1 ? "Ok" : "Bad");
                    ic.VerticalAlignment = VerticalAlignment.Top; ic.Margin = new Thickness(0, 2, 0, 0);
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = l.Item2, TextWrapping = TextWrapping.Wrap });
                    if (!string.IsNullOrEmpty(l.Item3)) sp.Children.Add(new TextBlock { Text = l.Item3, FontSize = 12.5, Foreground = Res(l.Item1 ? "Mut" : "Bad"), TextWrapping = TextWrapping.Wrap });
                    Grid.SetColumn(sp, 1);
                    g.Children.Add(ic); g.Children.Add(sp);
                    list.Children.Add(g);
                }
                var sv = new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                Body.Children.Add(sv);
                if (!string.IsNullOrEmpty(note))
                {
                    var n = new Border { Background = Res("AccSoft"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 0) };
                    n.Child = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                    Body.Children.Add(n);
                }
                var ok = Btn("Готово", "Primary");
                ok.HorizontalAlignment = HorizontalAlignment.Right; ok.Margin = new Thickness(0, 16, 0, 0); ok.MinWidth = 120;
                ok.Click += (s, e) => Win.CloseOverlay();
                Body.Children.Add(ok);
                ok.Focus();
            }
        }

        Overlay ShowOverlay(string title)
        {
            overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
            var ov = new Overlay { Win = this };
            var body = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            ov.Spin = Spinner(26);
            head.Children.Add(ov.Spin);
            ov.Title = new TextBlock { Text = title + "…", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            head.Children.Add(ov.Title);
            body.Children.Add(head);
            ov.Wait = new TextBlock { Text = "Подождите, некоторые настройки применяются до минуты. Не закрывайте программу.", Foreground = Res("Mut"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14) };
            body.Children.Add(ov.Wait);
            var stepLine = new Grid();
            stepLine.ColumnDefinitions.Add(new ColumnDefinition());
            stepLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ov.Step = new TextBlock { Text = "Запускаю…", TextTrimming = TextTrimming.CharacterEllipsis };
            ov.Counter = new TextBlock { Foreground = Res("Mut"), Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(ov.Counter, 1);
            stepLine.Children.Add(ov.Step); stepLine.Children.Add(ov.Counter);
            body.Children.Add(stepLine);
            ov.Bar = new ProgressBar { Style = St("Bar"), Maximum = 100, Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(ov.Bar);
            ov.Body = body;
            var card = Card(body, new Thickness(26, 22, 26, 22));
            card.Width = 520; card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.25 };
            overlay.Children.Add(card);
            root.Children.Add(overlay);
            currentOv = ov;
            FadeIn(overlay, 200);
            return ov;
        }

        void CloseOverlay()
        {
            if (overlay != null) root.Children.Remove(overlay);
            overlay = null;
            if (navPc.IsChecked == true) ShowMyPc();
        }

        Overlay currentOv;
        bool Busy { get { return currentOv != null && !currentOv.Finished; } }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!Busy && HideToTrayInstead()) { e.Cancel = true; return; }
            if (Busy)
            {
                if (MessageBox.Show(this, "Nolagi ещё работает. Закрыть всё равно?", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            }
            base.OnClosing(e);
            if (!e.Cancel) { StopFpsAny(); ShutdownGameMode(); }
            else reallyExit = false;
            if (!e.Cancel && mon != null) { if (monTimer != null) monTimer.Stop(); mon.Dispose(); mon = null; }   // nvidia-smi не должен остаться жить
        }

        // =================================================================== Чистка

        List<Dictionary<string, object>> cleanItems;
        readonly Dictionary<string, CheckBox> cleanChecks = new Dictionary<string, CheckBox>();
        Task<Dictionary<string, object>> cleanScan, cleanShown;   // one scan at a time, reused when the tab is reopened
        TextBlock cleanScanText;

        async void ShowClean()
        {
            if (cleanItems == null)
            {
                var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                wait.Children.Add(Spinner(36));
                var wt = new TextBlock { Text = "Считаю, сколько места занимает мусор…", Foreground = Res("Mut"), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                cleanScanText = new TextBlock { FontSize = 12, Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center };
                wait.Children.Add(wt); wait.Children.Add(cleanScanText);
                SetPage(PageFrame("Чистка", null, wait));
                if (cleanScan == null)
                    cleanScan = Bridge.Run("clean-list", null, m => Dispatcher.BeginInvoke(new Action(() => { if (J.S(m, "t") == "progress" && cleanScanText != null) cleanScanText.Text = J.S(m, "text"); })));
                var task = cleanScan;
                try
                {
                    var res = await task;
                    if (cleanItems == null) cleanItems = J.A(res, "items").Where(x => J.N(x, "sizeMb") != 0).ToList();
                }
                catch (Exception ex)
                {
                    if (cleanScan == task) cleanScan = null;
                    if (navClean.IsChecked == true) SetPage(PageFrame("Чистка", "Ошибка: " + ex.Message, new Grid()));
                    return;
                }
                if (cleanScan == task) cleanScan = null;
                if (navClean.IsChecked != true) return;
                if (cleanShown == task) return;   // another call already rendered this result
                cleanShown = task;
            }
            cleanChecks.Clear();
            var list = new StackPanel();
            foreach (var grp in cleanItems.GroupBy(x => J.S(x, "group")))
            {
                list.Children.Add(new TextBlock { Text = grp.Key, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 0, 8) });
                var cl = new StackPanel();
                bool first = true;
                foreach (var x in grp.OrderByDescending(x => J.N(x, "sizeMb")))
                {
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var c = new CheckBox { Style = St("Chk"), IsChecked = J.S(x, "risk") == "low", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                    cleanChecks[J.S(x, "id")] = c;
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = J.S(x, "title"), FontWeight = FontWeights.SemiBold });
                    if (J.S(x, "what").Length > 0) sp.Children.Add(new TextBlock { Text = J.S(x, "what"), FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                    if (J.S(x, "note").Length > 0) sp.Children.Add(new TextBlock { Text = J.S(x, "note"), FontSize = 12.5, Foreground = Res("Warn"), TextWrapping = TextWrapping.Wrap });
                    Grid.SetColumn(sp, 1);
                    var size = new TextBlock { Text = Mb(J.N(x, "sizeMb")), FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
                    Grid.SetColumn(size, 2);
                    g.Children.Add(c); g.Children.Add(sp); g.Children.Add(size);
                    var row = new Border { Padding = new Thickness(16, 12, 16, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Child = g };
                    first = false;
                    cl.Children.Add(row);
                }
                list.Children.Add(new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = cl, Margin = new Thickness(0, 0, 0, 16) });
            }
            if (cleanItems.Count == 0) list.Children.Add(T("Мусора не найдено — чисто.", 15, "Mut"));
            var sv = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };

            var bottom = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(0, 12, 8, 0) };
            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition());
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Mut") };
            var go = Btn("Очистить", "Primary");
            Action upd = () =>
            {
                var sel = cleanItems.Where(x => cleanChecks[J.S(x, "id")].IsChecked == true).ToList();
                double mb = sel.Sum(x => Math.Max(0, J.N(x, "sizeMb")));
                info.Text = "Выбрано: " + sel.Count + " · примерно " + Mb(mb);
                go.Content = "Очистить (" + Mb(mb) + ")";
                go.IsEnabled = sel.Count > 0;
            };
            foreach (var c in cleanChecks.Values) c.Click += (s, e) => upd();
            upd();
            go.Click += async (s, e) =>
            {
                var ids = cleanChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                var titles = cleanItems.ToDictionary(x => J.S(x, "id"), x => J.S(x, "title"));
                var ov = ShowOverlay("Чищу");
                var lines = new List<Tuple<bool, string, string>>();
                string head;
                try
                {
                    var res = await Bridge.Run("clean", ids, m => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        string t = J.S(m, "t");
                        if (t == "progress") ov.Progress(J.N(m, "i") - 1, J.N(m, "n"), J.S(m, "text"));
                        else if (t == "item") lines.Add(Tuple.Create(J.B(m, "ok"), titles.ContainsKey(J.S(m, "id")) ? titles[J.S(m, "id")] : J.S(m, "id"), J.S(m, "msg")));
                    })));
                    head = "Освобождено " + Mb(J.N(res, "freedMb"));
                }
                catch (Exception ex) { head = "Ошибка: " + ex.Message; }
                ov.Done(head, lines, "Занятые программами файлы пропускаются — это нормально.");
                cleanItems = null;
                ShowClean();
            };
            bg.Children.Add(info);
            Grid.SetColumn(go, 1); bg.Children.Add(go);
            bottom.Child = bg;
            SetPage(PageFrame("Чистка", "Временные файлы и кэши. Галочки уже стоят на безопасном; остальное — по желанию.", sv, bottom));
        }

        static string Mb(double mb)
        {
            if (mb < 0) return "?";
            return mb >= 1024 ? (mb / 1024).ToString("0.0") + " ГБ" : mb.ToString("0") + " МБ";
        }

        // =================================================================== Автозагрузка

        Task<Dictionary<string, object>> startupScan, startupShown;

        async void ShowStartup()
        {
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(Spinner(36));
            wait.Children.Add(new TextBlock { Text = "Смотрю, что запускается вместе с Windows…", Foreground = Res("Mut"), Margin = new Thickness(0, 14, 0, 0) });
            SetPage(PageFrame("Автозагрузка", null, wait));
            if (startupScan == null) startupScan = Bridge.Run("startup-list", null, null);
            var task = startupScan;
            List<Dictionary<string, object>> list;
            try { list = J.A(await task, "items"); }
            catch (Exception ex)
            {
                if (startupScan == task) startupScan = null;
                if (navStart.IsChecked == true) SetPage(PageFrame("Автозагрузка", "Ошибка: " + ex.Message, new Grid()));
                return;
            }
            if (startupScan == task) startupScan = null;
            if (navStart.IsChecked != true) return;
            if (startupShown == task) return;   // another call already rendered this result
            startupShown = task;

            var checks = new Dictionary<string, CheckBox>();
            var outer = new StackPanel();
            Func<string, string, List<Dictionary<string, object>>, StackPanel> section = (title, hint, rows) =>
            {
                var cl0 = new StackPanel();
                if (rows.Count == 0) return cl0;
                var head = new StackPanel { Margin = new Thickness(2, outer.Children.Count == 0 ? 0 : 18, 0, 8) };
                head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
                head.Children.Add(new TextBlock { Text = hint, FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                outer.Children.Add(head);
                outer.Children.Add(new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = cl0 });
                return cl0;
            };
            var apps = list.Where(x => J.S(x, "kind") != "task").ToList();
            var tasks = list.Where(x => J.S(x, "kind") == "task").ToList();
            var clApps = section("Программы", "Запускаются при входе в Windows.", apps);
            var clTasks = section("Задачи при входе и по расписанию", "Сторонние задачи планировщика: обновлялки и помощники программ. Системные задачи Windows здесь не показываются. Можно только включить или отключить, ничего не удаляется.", tasks);
            StackPanel cl = null;
            bool first = true;
            foreach (var x in apps.OrderBy(x => J.B(x, "enabled") ? 0 : 1).ThenBy(x => J.S(x, "name"))
                .Concat(tasks.OrderBy(x => J.B(x, "enabled") ? 0 : 1).ThenBy(x => J.S(x, "name"))))
            {
                var target = J.S(x, "kind") == "task" ? clTasks : clApps;
                if (target != cl) { cl = target; first = true; }
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var c = new CheckBox { Style = St("Chk"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                checks[J.S(x, "id")] = c;
                var sp = new StackPanel();
                var tl = new WrapPanel();
                tl.Children.Add(new TextBlock { Text = J.S(x, "name"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                string adv = J.S(x, "advice");
                if (adv == "off") tl.Children.Add(Pill("можно отключить", "AccSoft", "Acc", J.S(x, "adviceNote")));
                else if (adv == "keep") tl.Children.Add(Pill("лучше оставить", "WarnSoft", "Warn", J.S(x, "adviceNote")));
                sp.Children.Add(tl);
                string d = (J.S(x, "description") + (J.S(x, "publisher").Length > 0 ? " · " + J.S(x, "publisher") : "")).Trim(' ', '·');
                if (d.Length > 0) sp.Children.Add(new TextBlock { Text = d, FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                if (J.S(x, "kind") == "task" && J.S(x, "where").Length > 0)
                    sp.Children.Add(new TextBlock { Text = "Когда: " + J.S(x, "where").Replace("планировщик: ", ""), FontSize = 12.5, Foreground = Res("Txt"), TextWrapping = TextWrapping.Wrap });
                if (J.S(x, "adviceNote").Length > 0) sp.Children.Add(new TextBlock { Text = J.S(x, "adviceNote"), FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(sp, 1);
                bool en = J.B(x, "enabled");
                var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Background = en ? Res("OkSoft") : Res("Bg"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
                b.Child = new TextBlock { Text = en ? "Запускается" : "Отключено", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = en ? Res("Ok") : Res("Mut") };
                Grid.SetColumn(b, 2);
                g.Children.Add(c); g.Children.Add(sp); g.Children.Add(b);
                cl.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 12), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Child = g });
                first = false;
            }
            var sv = new ScrollViewer { Content = outer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };

            var bottom = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(0, 12, 8, 0) };
            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition());
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Mut") };
            var onB = Btn("Включить");
            var offB = Btn("Отключить", "Primary"); offB.Margin = new Thickness(8, 0, 0, 0);
            Action upd = () =>
            {
                int n = checks.Values.Count(c => c.IsChecked == true);
                info.Text = n == 0 ? "Отметьте программы или задачи" : "Выбрано: " + n;
                onB.IsEnabled = offB.IsEnabled = n > 0;
            };
            foreach (var c in checks.Values) c.Click += (s, e) => upd();
            upd();
            Func<string, Task> run = async (cmd) =>
            {
                var ids = checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                var ov = ShowOverlay(cmd == "startup-off" ? "Отключаю из автозагрузки" : "Включаю в автозагрузку");
                var lines = new List<Tuple<bool, string, string>>();
                string head;
                try
                {
                    var r = await Bridge.Run(cmd, ids, m => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        string t = J.S(m, "t");
                        if (t == "progress") ov.Progress(J.N(m, "i"), J.N(m, "n"), J.S(m, "text"));
                        else if (t == "item") lines.Add(Tuple.Create(J.B(m, "ok"), J.S(m, "name"), J.S(m, "msg")));
                    })));
                    head = "Готово: " + J.N(r, "ok") + (J.N(r, "failed") > 0 ? ", не получилось: " + J.N(r, "failed") : "");
                }
                catch (Exception ex) { head = "Ошибка: " + ex.Message; }
                ov.Done(head, lines, "Изменения вступят в силу при следующем входе в Windows.");
                ShowStartup();
            };
            onB.Click += async (s, e) => await run("startup-on");
            offB.Click += async (s, e) => await run("startup-off");
            var bs = new StackPanel { Orientation = Orientation.Horizontal };
            bs.Children.Add(onB); bs.Children.Add(offB);
            Grid.SetColumn(bs, 1);
            bg.Children.Add(info); bg.Children.Add(bs);
            bottom.Child = bg;
            SetPage(PageFrame("Автозагрузка", "Программы и задачи, которые стартуют вместе с Windows или по расписанию. Меньше лишнего — быстрее загрузка и меньше фона в игре.", sv, bottom));
        }

        // =================================================================== Проверка и ремонт

        Task<Dictionary<string, object>> healthScan;

        static Border Section(UIElement child, double top = 0)
        {
            return new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, top, 0, 0), Child = child };
        }

        static StackPanel SectionHead(string title, string hint, bool first)
        {
            var h = new StackPanel { Margin = new Thickness(2, first ? 0 : 20, 0, 8) };
            h.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(hint)) h.Children.Add(new TextBlock { Text = hint, FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
            return h;
        }

        /// One overlay run of a bridge command whose progress has no counter (n = 0 means «just show the text»).
        async Task<Dictionary<string, object>> RunTool(string title, string cmd, List<string> ids, List<Tuple<bool, string, string>> lines, Overlay ov)
        {
            return await Bridge.Run(cmd, ids, m => Dispatcher.BeginInvoke(new Action(() =>
            {
                string t = J.S(m, "t");
                if (t == "progress")
                {
                    if (J.N(m, "n") > 0) ov.Progress(J.N(m, "i"), J.N(m, "n"), J.S(m, "text"));
                    else { ov.Step.Text = J.S(m, "text"); ov.Counter.Text = ""; }
                }
                else if (t == "item" && lines != null) lines.Add(Tuple.Create(J.B(m, "ok"), J.S(m, "name"), J.S(m, "msg")));
            })));
        }

        async void ShowCheck()
        {
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(Spinner(36));
            wait.Children.Add(new TextBlock { Text = "Проверяю память, диск, видеокарту и настройки…", Foreground = Res("Mut"), Margin = new Thickness(0, 14, 0, 0) });
            SetPage(PageFrame("Проверка", null, wait));
            if (healthScan == null) healthScan = Bridge.Run("health", null, null);
            var task = healthScan;
            List<Dictionary<string, object>> found;
            try { found = J.A(await task, "items"); }
            catch (Exception ex)
            {
                if (healthScan == task) healthScan = null;
                if (navCheck.IsChecked == true) SetPage(PageFrame("Проверка", "Ошибка: " + ex.Message, new Grid()));
                return;
            }
            if (healthScan == task) healthScan = null;
            if (navCheck.IsChecked != true) return;

            var outer = new StackPanel();
            // ---- что нашёл Nolagi
            outer.Children.Add(SectionHead("Что нашёл Nolagi", "Сначала то, что стоит исправить. Nolagi ничего не меняет сам — только подсказывает.", true));
            var order = new Dictionary<string, int> { { "bad", 0 }, { "warn", 1 }, { "info", 2 }, { "ok", 3 } };
            var list = new StackPanel();
            bool first = true;
            foreach (var f in found.OrderBy(x => order.ContainsKey(J.S(x, "severity")) ? order[J.S(x, "severity")] : 9))
            {
                string sev = J.S(f, "severity");
                string label = sev == "bad" ? "исправить" : sev == "warn" ? "внимание" : sev == "ok" ? "в порядке" : "к сведению";
                string bg = sev == "bad" ? "BadSoft" : sev == "warn" ? "WarnSoft" : sev == "ok" ? "OkSoft" : "Bg";
                string fg = sev == "bad" ? "Bad" : sev == "warn" ? "Warn" : sev == "ok" ? "Ok" : "Mut";
                var sp = new StackPanel();
                var tl = new WrapPanel();
                tl.Children.Add(Pill(label, bg, fg, null));
                tl.Children.Add(new TextBlock { Text = J.S(f, "title"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(tl);
                if (J.S(f, "detail").Length > 0) sp.Children.Add(new TextBlock { Text = J.S(f, "detail"), FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                if (J.S(f, "hint").Length > 0) sp.Children.Add(new TextBlock { Text = "Что сделать: " + J.S(f, "hint"), FontSize = 12.5, Foreground = Res("Txt"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                if (J.S(f, "link") == "pckit:clean")
                {
                    var go = Btn("Открыть «Чистку»"); go.HorizontalAlignment = HorizontalAlignment.Left; go.Margin = new Thickness(0, 6, 0, 0);
                    go.Click += (s, e) => navClean.IsChecked = true;
                    sp.Children.Add(go);
                }
                list.Children.Add(new Border { Padding = new Thickness(0, first ? 0 : 10, 0, 10), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Child = sp });
                first = false;
            }
            outer.Children.Add(Section(list));

            // ---- ремонт Windows
            outer.Children.Add(SectionHead("Ремонт Windows", "Встроенные средства Windows: ничего не удаляют и не ломают. Помогают при вылетах, синих экранах и пропавшем интернете.", false));
            var rep = new StackPanel();
            Func<string, string, Button, Grid> toolRow = (title, text, btn) =>
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var t = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                t.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                btn.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(btn, 1);
                g.Children.Add(t); g.Children.Add(btn);
                return g;
            };
            var sfcBtn = Btn("Проверить и починить");
            sfcBtn.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "Windows проверит и восстановит свои системные файлы (DISM и SFC).\n\nЭто занимает 10–30 минут, нужен интернет. Можно пользоваться ПК, но лучше не играть. Продолжить?", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var ov = ShowOverlay("Ремонт системных файлов");
                ov.Step.Text = "Запускаю…"; ov.Counter.Text = "";
                string head, note;
                try
                {
                    var r = await RunTool("", "repair-system", null, null, ov);
                    string sfc = J.S(r, "sfc");
                    bool dismOk = J.N(r, "dism") == 0;
                    head = sfc == "clean" ? "Повреждений не найдено" : sfc == "repaired" ? "Найдены и исправлены повреждения" : sfc == "broken" ? "Часть файлов исправить не удалось" : "Проверка завершена";
                    note = (dismOk ? "Хранилище компонентов в порядке. " : "DISM не смог проверить хранилище (код " + J.N(r, "dism") + ") — нужен интернет. ")
                        + (sfc == "repaired" ? "Перезагрузите ПК." : sfc == "broken" ? "Перезагрузите ПК и запустите проверку ещё раз. Если не поможет — нужна переустановка Windows поверх (с сохранением файлов)." : "");
                }
                catch (Exception ex) { head = "Ошибка: " + ex.Message; note = ""; }
                ov.Done(head, new List<Tuple<bool, string, string>>(), note);
            };
            var netBtn = Btn("Сбросить сеть");
            netBtn.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "Сброс вернёт сетевые настройки Windows к заводским: Winsock, TCP/IP и кэш DNS.\n\nПомогает, когда пропал интернет или не грузятся сайты. Ручные настройки IP, DNS и некоторых VPN придётся задать заново. После сброса нужна перезагрузка.\n\nПродолжить?", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var ov = ShowOverlay("Сброс сети");
                var lines = new List<Tuple<bool, string, string>>();
                string head;
                try { var r = await RunTool("", "repair-network", null, lines, ov); head = J.N(r, "failed") > 0 ? "Сделано не всё" : "Сеть сброшена"; }
                catch (Exception ex) { head = "Ошибка: " + ex.Message; }
                ov.Done(head, lines, "Перезагрузите ПК, чтобы сброс вступил в силу.");
            };
            rep.Children.Add(toolRow("Системные файлы Windows", "Проверка и восстановление (DISM + SFC). Если игры или Windows вылетают без причины. 10–30 минут.", sfcBtn));
            rep.Children.Add(new Border { Height = 1, Background = Res("Line"), Margin = new Thickness(0, 6, 0, 6) });
            rep.Children.Add(toolRow("Сеть", "Сброс Winsock, TCP/IP и кэша DNS. Если пропал интернет или не грузятся сайты и лаунчеры.", netBtn));
            outer.Children.Add(Section(rep));

            // ---- DNS
            outer.Children.Add(SectionHead("Быстрый DNS", "DNS превращает адрес сайта в IP. Быстрый DNS ускоряет открытие сайтов, Steam и лаунчеров. Пинг в самой игре он не меняет.", false));
            var dnsBox = new StackPanel();
            var benchBtn = Btn("Замерить скорость DNS", "Primary"); benchBtn.HorizontalAlignment = HorizontalAlignment.Left;
            dnsBox.Children.Add(benchBtn);
            benchBtn.Click += async (s, e) => await RunDnsBench(dnsBox);
            outer.Children.Add(Section(dnsBox));

            var sv = new ScrollViewer { Content = outer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
            SetPage(PageFrame("Проверка", "Что мешает играм на этом ПК, ремонт Windows и выбор быстрого DNS.", sv));
        }

        async Task RunDnsBench(StackPanel box)
        {
            box.Children.Clear();
            var w = new StackPanel { Orientation = Orientation.Horizontal };
            w.Children.Add(Spinner(20));
            var wt = new TextBlock { Text = "  Замеряю…", Foreground = Res("Mut"), VerticalAlignment = VerticalAlignment.Center };
            w.Children.Add(wt);
            box.Children.Add(w);
            Dictionary<string, object> r;
            try
            {
                r = await Bridge.Run("dns-bench", null, m => Dispatcher.BeginInvoke(new Action(() => { if (J.S(m, "t") == "progress") wt.Text = "  Замеряю: " + J.S(m, "text"); })));
            }
            catch (Exception ex)
            {
                box.Children.Clear();
                box.Children.Add(new TextBlock { Text = "Ошибка: " + ex.Message, Foreground = Res("Bad"), TextWrapping = TextWrapping.Wrap });
                return;
            }
            box.Children.Clear();
            var rows = J.A(r, "items");
            var ok = rows.Where(x => J.N(x, "ms") >= 0).OrderBy(x => J.N(x, "ms")).ToList();
            var best = ok.FirstOrDefault();
            bool win11 = J.B(r, "win11");
            if (rows.Count == 0 || best == null)
            {
                box.Children.Add(new TextBlock { Text = "Ни один DNS не ответил. Проверьте интернет.", Foreground = Res("Warn") });
                return;
            }
            var cur = rows.FirstOrDefault(x => J.B(x, "current"));
            string summary;
            if (J.B(best, "current")) summary = "Ваш текущий DNS отвечает быстрее всех — менять не нужно.";
            else if (cur != null && J.N(cur, "ms") >= 0 && J.N(cur, "ms") - J.N(best, "ms") < 10) summary = "Разница меньше 10 мс — менять DNS не обязательно.";
            else summary = "Быстрее всего: " + J.S(best, "name") + ". Можно выбрать его кнопкой ниже.";
            box.Children.Add(new TextBlock { Text = summary, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

            var doh = new CheckBox { Style = St("Chk"), IsChecked = true, Margin = new Thickness(0, 0, 0, 8), Visibility = win11 ? Visibility.Visible : Visibility.Collapsed };
            doh.Content = new TextBlock { Text = "Шифровать запросы (DNS-over-HTTPS) — провайдер не видит, какие сайты вы открываете", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var x in rows.OrderBy(x => J.N(x, "ms") < 0 ? 99999 : J.N(x, "ms")))
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var t = new StackPanel();
                var tl = new WrapPanel();
                tl.Children.Add(new TextBlock { Text = J.S(x, "name"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                if (x == best) tl.Children.Add(Pill("самый быстрый", "OkSoft", "Ok", null));
                t.Children.Add(tl);
                t.Children.Add(new TextBlock { Text = J.S(x, "servers") + (J.S(x, "note").Length > 0 ? " · " + J.S(x, "note") : ""), FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                double ms = J.N(x, "ms");
                var msT = new TextBlock { Text = ms < 0 ? "нет ответа" : ms + " мс", VerticalAlignment = VerticalAlignment.Center, Foreground = ms < 0 ? Res("Bad") : Res("Txt"), FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(msT, 1);
                g.Children.Add(t); g.Children.Add(msT);
                if (!J.B(x, "current") && ms >= 0)
                {
                    var pick = Btn("Выбрать"); pick.VerticalAlignment = VerticalAlignment.Center;
                    string key = J.S(x, "key"), name = J.S(x, "name");
                    bool canDoh = J.B(x, "doh");
                    pick.Click += async (s, e) =>
                    {
                        var ids = new List<string> { key };
                        if (canDoh && win11 && doh.IsChecked == true) ids.Add("doh");
                        var ov = ShowOverlay("Меняю DNS на " + name);
                        ov.Step.Text = "Меняю настройки сетевых адаптеров"; ov.Counter.Text = "";
                        var lines = new List<Tuple<bool, string, string>>();
                        string head;
                        try
                        {
                            var res = await RunTool("", "dns-set", ids, lines, ov);
                            undoCount = (int)J.N(res, "undoCount");
                            head = J.N(res, "failed") > 0 ? "Сделано не всё" : "DNS изменён на " + name;
                        }
                        catch (Exception ex) { head = "Ошибка: " + ex.Message; }
                        ov.Done(head, lines, "Вернуть прежний DNS можно во вкладке «Откат».");
                        await RunDnsBench(box);
                    };
                    Grid.SetColumn(pick, 2);
                    g.Children.Add(pick);
                }
                box.Children.Add(g);
            }
            box.Children.Add(doh);
            var again = new Button { Content = "Замерить ещё раз", Style = St("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            again.Click += async (s, e) => await RunDnsBench(box);
            box.Children.Add(again);
        }

        // =================================================================== Встроенные приложения

        async void ShowApps()
        {
            var wait = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wait.Children.Add(Spinner(36));
            wait.Children.Add(new TextBlock { Text = "Ищу встроенные приложения…", Foreground = Res("Mut"), Margin = new Thickness(0, 14, 0, 0) });
            SetPage(PageFrame("Приложения", null, wait));
            List<Dictionary<string, object>> list;
            try { list = J.A(await Bridge.Run("apps-list", null, null), "items"); }
            catch (Exception ex)
            {
                if (navApps.IsChecked == true) SetPage(PageFrame("Приложения", "Ошибка: " + ex.Message, new Grid()));
                return;
            }
            if (navApps.IsChecked != true) return;
            const string sub = "Предустановленные приложения Windows, которыми почти никто не пользуется. Системные, Магазин, драйверы и Xbox (нужны для Game Pass) здесь не показываются.";
            if (list.Count == 0)
            {
                var empty = new TextBlock { Text = "Лишних встроенных приложений не найдено — всё уже чисто.", Foreground = Res("Mut"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                SetPage(PageFrame("Приложения", sub, empty));
                return;
            }
            var checks = new Dictionary<string, CheckBox>();
            var cl = new StackPanel();
            bool first = true;
            foreach (var x in list.OrderBy(x => J.S(x, "level") == "safe" ? 0 : 1).ThenBy(x => J.S(x, "name")))
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var c = new CheckBox { Style = St("Chk"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                checks[J.S(x, "id")] = c;
                var sp = new StackPanel();
                var tl = new WrapPanel();
                tl.Children.Add(new TextBlock { Text = J.S(x, "name"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                if (J.S(x, "level") == "safe") tl.Children.Add(Pill("можно удалить", "AccSoft", "Acc", null));
                else tl.Children.Add(Pill("если пользуетесь — оставьте", "WarnSoft", "Warn", null));
                sp.Children.Add(tl);
                if (J.S(x, "level") != "safe" && J.S(x, "note").Length > 0 && J.S(x, "id") == "ONEDRIVE")
                    sp.Children.Add(new TextBlock { Text = J.S(x, "note"), FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(sp, 1);
                g.Children.Add(c); g.Children.Add(sp);
                cl.Children.Add(new Border { Padding = new Thickness(16, 11, 16, 11), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Child = g });
                first = false;
            }
            var body = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = cl, VerticalAlignment = VerticalAlignment.Top };
            var sv = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };

            var bottom = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(0, 12, 8, 0) };
            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition());
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Mut") };
            var safeB = new Button { Content = "Отметить «можно удалить»", Style = St("Link"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var delB = Btn("Удалить", "Danger");
            Action upd = () =>
            {
                int n = checks.Values.Count(c => c.IsChecked == true);
                info.Text = n == 0 ? "Отметьте приложения" : "Выбрано: " + n;
                delB.IsEnabled = n > 0;
            };
            foreach (var c in checks.Values) c.Click += (s, e) => upd();
            safeB.Click += (s, e) => { foreach (var x in list) if (J.S(x, "level") == "safe") checks[J.S(x, "id")].IsChecked = true; upd(); };
            upd();
            delB.Click += async (s, e) =>
            {
                var ids = checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                if (MessageBox.Show(this, "Удалить выбранные приложения (" + ids.Count + ")?\n\nКнопкой «Откат» их не вернуть — только заново установить из Microsoft Store. Ваши файлы не затрагиваются.", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var ov = ShowOverlay("Удаляю приложения");
                var lines = new List<Tuple<bool, string, string>>();
                string head;
                try { var r = await RunTool("", "apps-remove", ids, lines, ov); head = "Удалено: " + J.N(r, "ok") + (J.N(r, "failed") > 0 ? ", не получилось: " + J.N(r, "failed") : ""); }
                catch (Exception ex) { head = "Ошибка: " + ex.Message; }
                ov.Done(head, lines, "Вернуть приложение можно из Microsoft Store.");
                ShowApps();
            };
            var bs = new StackPanel { Orientation = Orientation.Horizontal };
            bs.Children.Add(safeB); bs.Children.Add(delB);
            Grid.SetColumn(bs, 1);
            bg.Children.Add(info); bg.Children.Add(bs);
            bottom.Child = bg;
            SetPage(PageFrame("Приложения", sub, sv, bottom));
        }

        // =================================================================== Откат

        void ShowUndo()
        {
            var sp = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            var c1 = new StackPanel();
            c1.Children.Add(new TextBlock { Text = "Отдельные настройки", FontSize = 17, FontWeight = FontWeights.SemiBold });
            c1.Children.Add(new TextBlock { Text = "Во вкладке «Настройки» отметьте включённые пункты и нажмите «Откатить». Так можно вернуть только то, что мешает.", Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
            var goSet = Btn("Открыть настройки"); goSet.HorizontalAlignment = HorizontalAlignment.Left;
            goSet.Click += (s, e) => navSet.IsChecked = true;
            c1.Children.Add(goSet);
            var card1 = Card(c1); card1.Margin = new Thickness(0, 0, 0, 12);
            sp.Children.Add(card1);

            var c2 = new StackPanel();
            c2.Children.Add(new TextBlock { Text = "Вернуть всё как было", FontSize = 17, FontWeight = FontWeights.SemiBold });
            c2.Children.Add(new TextBlock { Text = undoCount > 0 ? "Nolagi вернёт все свои изменения (" + undoCount + ") к состоянию до первого запуска. Файлы, удалённые чисткой, не вернуть." : "Nolagi пока ничего не менял — откатывать нечего.", Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
            var all = Btn("Вернуть всё", "Danger"); all.HorizontalAlignment = HorizontalAlignment.Left; all.IsEnabled = undoCount > 0;
            all.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "Вернуть все изменения Nolagi как было?", "Nolagi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var ov = ShowOverlay("Возвращаю всё как было");
                var lines = new List<Tuple<bool, string, string>>();
                string head;
                try
                {
                    var r = await Bridge.Run("undoall", null, m => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (J.S(m, "t") == "progress") ov.Progress(0, 1, J.S(m, "text"));
                        else if (J.S(m, "t") == "item" && !J.B(m, "ok")) lines.Add(Tuple.Create(false, J.S(m, "id"), J.S(m, "msg")));
                    })));
                    undoCount = (int)J.N(r, "undoCount");
                    head = "Возвращено: " + J.N(r, "ok") + (J.N(r, "failed") > 0 ? ", не получилось: " + J.N(r, "failed") : "");
                    Telemetry.Send("rollback", new[] { new KeyValuePair<string, bool>("ALL", J.N(r, "failed") == 0) });
                }
                catch (Exception ex) { head = "Ошибка: " + ex.Message; }
                // state changed everywhere - re-read it
                try { var st = await Bridge.Run("state", null, null); BuildItems(J.A(st, "actions")); } catch { }
                ov.Done(head, lines, "Перезагрузите компьютер, чтобы всё вернулось полностью.");
                ShowUndo();
            };
            c2.Children.Add(all);
            sp.Children.Add(Card(c2));
            SetPage(PageFrame("Откат", "Всё, что меняет Nolagi, записывается — любое изменение можно отменить.", sp));
        }

        // =================================================================== first run: nick + consent

        void AskConsentOnce()
        {
            if (cfg.Asked) return;
            var dlg = new Window
            {
                Owner = this, Title = "Nolagi", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Res("Bg"), FontFamily = FontFamily, FontSize = 14
            };
            var sp = new StackPanel { Margin = new Thickness(24) };
            sp.Children.Add(new TextBlock { Text = "Добро пожаловать!", FontSize = 20, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = "Как вас называть? Ник увидит только автор программы.", Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
            var nick = new TextBox { Style = St("Search"), MaxLength = 32 };
            sp.Children.Add(nick);
            // opt-in: the box starts empty, and closing the window with X means «no»
            var agree = new CheckBox { Style = St("Chk"), IsChecked = false, Margin = new Thickness(0, 16, 0, 0) };
            agree.Content = new TextBlock { Text = "Отправлять автору статистику: ник, модель процессора и видеокарты, объём памяти, версию Windows и какие настройки применены. Сервер также видит ваш IP-адрес. Файлы и пароли не отправляются. Можно не включать — программа работает так же. Изменить выбор можно на вкладке «Написать автору».", TextWrapping = TextWrapping.Wrap, Width = 370, FontSize = 13 };
            sp.Children.Add(agree);
            sp.Children.Add(new TextBlock { Text = "При каждом запуске Nolagi проверяет обновления; сервер анонимно считает, сколько ПК открыли программу за день — без ника и без сохранения IP.", Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0) });
            // honest disclaimer: the program changes Windows settings
            var warn = new Border { Background = Res("WarnSoft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0) };
            warn.Child = new TextBlock { Text = "Nolagi меняет настройки Windows. Перед изменениями создаётся точка восстановления, и любую настройку можно откатить во вкладке «Откат». Программа бесплатная и даётся «как есть»: пользуясь ею, вы соглашаетесь, что делаете это на свой риск.", TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Res("Txt") };
            sp.Children.Add(warn);
            bool confirmed = false;
            var ok = Btn("Продолжить", "Primary"); ok.HorizontalAlignment = HorizontalAlignment.Right; ok.Margin = new Thickness(0, 20, 0, 0); ok.IsDefault = true;
            ok.Click += (s, e) => { confirmed = true; dlg.Close(); };
            sp.Children.Add(ok);
            dlg.Content = sp;
            dlg.ShowDialog();
            cfg.Nick = confirmed ? (nick.Text ?? "").Trim() : "";
            cfg.Consent = confirmed && agree.IsChecked == true;
            cfg.Asked = true;
            cfg.Save();
        }
    }
}
