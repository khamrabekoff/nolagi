// Nolagi.exe - вкладка «Игровой режим», значок в трее и автозапуск.
// Логика (что закрыть, что вернуть) — в GameModeEngine.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pckit
{
    public partial class MainWindow
    {
        static readonly string DevSuffix = Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location).IndexOf("-dev", StringComparison.OrdinalIgnoreCase) >= 0 ? ".dev" : "";
        public static readonly string ShowEventName = "Local\\Nolagi.Show" + DevSuffix;   // вторая копия просит показать окно
        public static readonly string ExitEventName = "Local\\Nolagi.Exit" + DevSuffix;   // скачанная новая версия просит выйти

        RadioButton navGame;
        readonly GameModeEngine gm = new GameModeEngine();
        DispatcherTimer gmTimer;
        bool gmBusy;
        TextBlock gmStatus;
        System.Windows.Forms.NotifyIcon tray;
        System.Windows.Forms.ToolStripMenuItem trayGameItem;
        bool reallyExit, sessionEnding;
        bool startHidden, neverShown;
        bool autoCache;   // последнее известное состояние автозапуска — чтобы галочка не мигала при перерисовке

        /// Вызывается из конструктора: запуск в трее и сигнал «покажись» от второй копии Nolagi.
        void InitInstance(bool hidden)
        {
            startHidden = neverShown = hidden;
            if (hidden)
            {
                ShowInTaskbar = false; ShowActivated = false;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -32000; Top = -32000;
            }
            Application.Current.SessionEnding += (s, e) => { sessionEnding = true; reallyExit = true; };
            try
            {
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                var t = new Thread(() => { while (true) { ev.WaitOne(); Dispatcher.BeginInvoke(new Action(ShowFromTray)); } }) { IsBackground = true };
                t.Start();
                var ex = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                var t2 = new Thread(() =>
                {
                    ex.WaitOne();
                    // выходим по-настоящему: закрытые игровым режимом программы вернутся, приоритеты восстановятся
                    Dispatcher.BeginInvoke(new Action(() => { reallyExit = true; Close(); }));
                }) { IsBackground = true };
                t2.Start();
            }
            catch { }
        }

        /// После построения окна: игровой режим, если он был включён.
        void InitGameMode()
        {
            if (startHidden || cfg.GameModeOn) EnsureTray();
            // сначала вернуть то, что не вернул прошлый запуск (если он упал посреди игры), потом следить за играми
            Task.Run(() => GameModeEngine.RecoverAfterCrash())
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { if (cfg.GameModeOn) StartGameMode(); })));
        }

        GameModeConfig GmSnapshot()
        {
            var c = new GameModeConfig { MemClean = cfg.MemCleanOn };
            foreach (var g in GameRules.BuiltInGames.Keys) c.Games.Add(g);
            foreach (var g in cfg.Games) c.Games.Add(g);
            foreach (var kv in cfg.GameRules) c.Rules[kv.Key] = kv.Value;
            return c;
        }

        void StartGameMode()
        {
            if (gmTimer == null)
            {
                gmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                gmTimer.Tick += (s, e) => GmTick();
            }
            gmTimer.Start();
            GmTick();
        }

        void StopGameMode(bool restartApps)
        {
            if (gmTimer != null) gmTimer.Stop();
            // синхронно: при выходе закрытые программы должны вернуться, а приоритеты — восстановиться
            try { gm.End(restartApps); } catch { }
            RenderGmStatus();
        }

        void GmTick()
        {
            if (gmBusy) return;
            gmBusy = true;
            var snap = GmSnapshot();
            Task.Run(() => { try { gm.Tick(snap); } catch (Exception ex) { Telemetry.Error("gamemode", ex.Message); } })
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { gmBusy = false; RenderGmStatus(); })));
        }

        void RenderGmStatus()
        {
            string s = cfg.GameModeOn ? gm.Status : "Выключен.";
            if (gmStatus != null)
            {
                gmStatus.Text = s;
                gmStatus.Foreground = Res(cfg.GameModeOn && gm.Game != null ? "Ok" : "Mut");
            }
            if (tray != null)
            {
                string tip = "Nolagi — игровой режим: " + s;
                tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;   // ограничение Windows
                if (trayGameItem != null) trayGameItem.Checked = cfg.GameModeOn;
            }
        }

        void SetGameMode(bool on)
        {
            cfg.GameModeOn = on; cfg.Save();
            if (on) { StartGameMode(); EnsureTray(); }
            else
            {
                Task.Run(() => gm.End(true));
                if (gmTimer != null) gmTimer.Stop();
                if (IsVisible) RemoveTray();
            }
            RenderGmStatus();
        }

        // ---------------------------------------------------------------- трей

        void EnsureTray()
        {
            if (tray != null) return;
            try
            {
                tray = new System.Windows.Forms.NotifyIcon();
                try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Открыть Nolagi", null, (s, e) => ShowFromTray());
                trayGameItem = new System.Windows.Forms.ToolStripMenuItem("Игровой режим") { CheckOnClick = false, Checked = cfg.GameModeOn };
                trayGameItem.Click += (s, e) => { SetGameMode(!cfg.GameModeOn); if (navGame != null && navGame.IsChecked == true) ShowGameMode(); };
                menu.Items.Add(trayGameItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Выход", null, (s, e) => { reallyExit = true; Close(); });
                tray.ContextMenuStrip = menu;
                tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
                tray.Visible = true;
                RenderGmStatus();
            }
            catch (Exception ex) { Telemetry.Error("tray", ex.Message); tray = null; }
        }

        void RemoveTray()
        {
            if (tray == null) return;
            tray.Visible = false;
            tray.Dispose();
            tray = null; trayGameItem = null;
        }

        void ShowFromTray()
        {
            if (neverShown)
            {
                neverShown = false;
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                ShowInTaskbar = true; ShowActivated = true;
            }
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;   // поверх остальных окон, но не навсегда
            if (!cfg.GameModeOn) RemoveTray();
        }

        /// Крестик при включённом игровом режиме: прячемся в трей, иначе игровой режим перестанет работать.
        bool HideToTrayInstead()
        {
            if (reallyExit || !cfg.GameModeOn) return false;
            EnsureTray();
            if (tray == null) return false;
            Hide();
            if (navMon != null && navMon.IsChecked == true && navPc != null) navPc.IsChecked = true;   // мониторинг в фоне не нужен
            if (!cfg.TrayHintShown)
            {
                cfg.TrayHintShown = true; cfg.Save();
                tray.ShowBalloonTip(5000, "Nolagi работает в трее", "Игровой режим включён. Чтобы выйти совсем — правый клик по значку → «Выход».", System.Windows.Forms.ToolTipIcon.Info);
            }
            return true;
        }

        void ShutdownGameMode()
        {
            StopGameMode(!sessionEnding);
            RemoveTray();
        }

        // ---------------------------------------------------------------- вкладка

        void ShowGameMode()
        {
            var outer = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

            // ---- главный переключатель
            var main = new StackPanel();
            var on = new CheckBox { Style = St("Chk"), IsChecked = cfg.GameModeOn };
            on.Content = new TextBlock { Text = "Включить игровой режим", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
            on.Click += (s, e) => SetGameMode(on.IsChecked == true);
            main.Children.Add(on);
            gmStatus = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 4, 0, 0) };
            main.Children.Add(gmStatus);
            main.Children.Add(new TextBlock
            {
                Text = "Когда вы запускаете игру, Nolagi закрывает или притормаживает программы из списка ниже, а после игры возвращает всё как было: закрытые программы запускаются снова. Работает, пока Nolagi открыт или свёрнут в трей.",
                FontSize = 13, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 8, 0, 0)
            });

            // автозапуск
            var auto = new CheckBox { Style = St("Chk"), Margin = new Thickness(0, 14, 0, 0), IsEnabled = false, IsChecked = autoCache };
            if (Autostart.Available)
            {
                // schtasks.exe — отдельный процесс: спрашиваем в фоне, чтобы вкладка не подвисала
                Task.Run(() => { try { return Autostart.On; } catch { return false; } })
                    .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { autoCache = t.Result; auto.IsChecked = t.Result; auto.IsEnabled = true; })));
            }
            auto.Content = new TextBlock { Text = "Запускать Nolagi вместе с Windows (сразу в трей)", Margin = new Thickness(8, 0, 0, 0) };
            var autoNote = new TextBlock { FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 2, 0, 0) };
            autoNote.Text = Autostart.Available
                ? "Тогда игровой режим сработает, даже если вы не открывали Nolagi. Окно не появляется — только значок у часов."
                : "Доступно в установленной версии Nolagi (из папки Program Files).";
            auto.Click += async (s, e) =>
            {
                bool want = auto.IsChecked == true;
                auto.IsEnabled = false;
                bool ok = await Task.Run(() => { try { return Autostart.Set(want); } catch { return false; } });
                bool now = await Task.Run(() => { try { return Autostart.On; } catch { return false; } });
                auto.IsChecked = now; auto.IsEnabled = true; autoCache = now;
                if (!ok) { autoNote.Text = "Не получилось изменить автозапуск. Попробуйте ещё раз."; autoNote.Foreground = Res("Bad"); }
                else if (want && !cfg.GameModeOn) { SetGameMode(true); on.IsChecked = true; }
            };
            main.Children.Add(auto);
            main.Children.Add(autoNote);

            // ждущая память
            bool suits = MemPurge.Suits;
            double gb = MemPurge.TotalGb;
            var mem = new CheckBox { Style = St("Chk"), Margin = new Thickness(0, 14, 0, 0), IsEnabled = suits, IsChecked = suits && cfg.MemCleanOn };
            mem.Content = new TextBlock { Text = "Очищать «ждущую» память во время игры", Margin = new Thickness(8, 0, 0, 0) };
            mem.Click += (s, e) => { cfg.MemCleanOn = mem.IsChecked == true; cfg.Save(); };
            main.Children.Add(mem);
            string gbText = gb > 0 ? Math.Round(gb) + " ГБ" : "неизвестно сколько";
            main.Children.Add(new TextBlock
            {
                Text = suits
                    ? "У вас " + gbText + " памяти. Windows держит в памяти кэш файлов и не всегда успевает отдать его игре — отсюда фризы при подгрузке карты. Nolagi очищает этот кэш, только когда свободной памяти меньше 1 ГБ, и не чаще раза в 2 минуты (как ISLC)."
                    : gb > 17 ? "У вас " + gbText + " памяти — Windows хватает её и так, очистка не нужна и не поможет."
                              : "Нужны права администратора.",
                FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 2, 0, 0)
            });
            outer.Children.Add(Card(main));

            // ---- игры
            var gsp = new StackPanel();
            gsp.Children.Add(SectionHead("Игры", "Игровой режим включается, когда запущена одна из этих игр.", true));
            var chips = new WrapPanel();
            foreach (var kv in GameRules.BuiltInGames) chips.Children.Add(GameChip(kv.Value, null));
            foreach (var g in cfg.Games.ToList())
            {
                string exe = g;
                chips.Children.Add(GameChip(Path.GetFileNameWithoutExtension(exe), () => { cfg.Games.Remove(exe); cfg.Save(); ShowGameMode(); }));
            }
            gsp.Children.Add(chips);
            var add = Btn("Добавить игру…"); add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 10, 0, 0);
            add.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выберите exe-файл игры", Filter = "Программы (*.exe)|*.exe" };
                if (dlg.ShowDialog(this) != true) return;
                string exe = Path.GetFileName(dlg.FileName).ToLowerInvariant();
                if (GameRules.Locked.Contains(exe)) { MessageBox.Show(this, "Это не игра, а системная программа или лаунчер — её нельзя добавить.", "Nolagi", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (!cfg.Games.Contains(exe) && !GameRules.BuiltInGames.ContainsKey(exe)) { cfg.Games.Add(exe); cfg.Save(); }
                ShowGameMode();
            };
            gsp.Children.Add(add);
            var gc = Card(gsp); gc.Margin = new Thickness(0, 14, 0, 0);
            outer.Children.Add(gc);

            // ---- программы
            var psp = new StackPanel();
            psp.Children.Add(SectionHead("Что делать с программами во время игры",
                "«Закрыть» — программа закроется и запустится снова после игры. «Ниже приоритет» — останется открытой, но будет уступать процессор игре. «Не трогать» — ничего не меняется.", true));
            var list = new StackPanel();
            psp.Children.Add(list);
            var refresh = Btn("Обновить список", "Link"); refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Margin = new Thickness(0, 8, 0, 0);
            refresh.Click += async (s, e) => await FillGmApps(list);
            psp.Children.Add(refresh);
            psp.Children.Add(new TextBlock
            {
                Text = "Steam, FACEIT, античиты и системные программы Windows Nolagi не трогает никогда, поэтому их нет в списке.",
                FontSize = 12.5, Foreground = Res("Mut"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
            var pc = Card(psp); pc.Margin = new Thickness(0, 14, 0, 0);
            outer.Children.Add(pc);

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outer };
            SetPage(PageFrame("Игровой режим", "Меньше лишнего в фоне, пока вы играете. После игры всё возвращается.", sv));
            RenderGmStatus();
            var _ = FillGmApps(list);
        }

        Border GameChip(string text, Action remove)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Glyph("", 14, "Acc"));
            sp.Children.Add(new TextBlock { Text = "  " + text, VerticalAlignment = VerticalAlignment.Center });
            if (remove != null)
            {
                var x = new Button { Content = "✕", Style = St("Link"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Убрать из списка", VerticalAlignment = VerticalAlignment.Center };
                x.Click += (s, e) => remove();
                sp.Children.Add(x);
            }
            return new Border { Background = Res("AccSoft"), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 8), Child = sp };
        }

        class GmApp { public string Exe, Name, Path; public bool Running; }

        async Task FillGmApps(StackPanel list)
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = "Смотрю, какие программы запущены…", Foreground = Res("Mut"), Margin = new Thickness(0, 4, 0, 4) });
            var known = new HashSet<string>(cfg.GameRules.Keys, StringComparer.OrdinalIgnoreCase);
            var games = GmSnapshot().Games;
            List<GmApp> apps = await Task.Run(() => CollectGmApps(known, games));
            list.Children.Clear();
            foreach (var a in apps) list.Children.Add(GmRow(a));
            if (apps.Count == 0) list.Children.Add(new TextBlock { Text = "Подходящих программ не найдено.", Foreground = Res("Mut") });
        }

        static List<GmApp> CollectGmApps(HashSet<string> known, HashSet<string> games)
        {
            int session = Process.GetCurrentProcess().SessionId;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var res = new Dictionary<string, GmApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId != session) continue;
                        string exe = GameModeEngine.ExeName(p);
                        if (exe == null || res.ContainsKey(exe) || GameRules.Locked.Contains(exe) || games.Contains(exe)) continue;
                        bool listed = known.Contains(exe) || GameRules.Defaults.ContainsKey(exe);
                        if (!listed && p.MainWindowHandle == IntPtr.Zero) continue;   // фоновые процессы без окна показываем, только если о них уже есть правило
                        string path = ProcInfo.ImagePath(p.Id);
                        if (path == null || path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) continue;   // части Windows не трогаем
                        res[exe] = new GmApp { Exe = exe, Path = path, Name = ProcInfo.Describe(path, exe), Running = true };
                    }
                    catch { }
                }
            }
            // программы с правилом, которые сейчас не запущены, тоже показываем — чтобы правило можно было поменять
            foreach (var exe in known.Concat(GameRules.Defaults.Keys))
                if (!res.ContainsKey(exe) && !games.Contains(exe)) res[exe] = new GmApp { Exe = exe, Name = KnownName(exe), Running = false };
            return res.Values.OrderBy(a => a.Running ? 0 : 1).ThenBy(a => a.Running ? a.Name : KnownName(a.Exe), StringComparer.OrdinalIgnoreCase).ToList();
        }

        static readonly Dictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "epicgameslauncher.exe", "Epic Games Launcher" }, { "eadesktop.exe", "EA app" }, { "upc.exe", "Ubisoft Connect" },
            { "battle.net.exe", "Battle.net" }, { "galaxyclient.exe", "GOG Galaxy" }, { "onedrive.exe", "OneDrive" },
            { "chrome.exe", "Google Chrome" }, { "msedge.exe", "Microsoft Edge" }, { "firefox.exe", "Firefox" }, { "browser.exe", "Яндекс Браузер" },
            { "opera.exe", "Opera" }, { "qbittorrent.exe", "qBittorrent" }, { "utorrent.exe", "µTorrent" },
            { "discord.exe", "Discord" }, { "obs64.exe", "OBS Studio" }, { "telegram.exe", "Telegram" }, { "spotify.exe", "Spotify" }
        };

        static string KnownName(string exe)
        {
            string n;
            return KnownNames.TryGetValue(exe, out n) ? n : Path.GetFileNameWithoutExtension(exe);
        }

        Border GmRow(GmApp a)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            string shown = a.Running ? a.Name : KnownName(a.Exe);
            name.Children.Add(new TextBlock { Text = shown, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = a.Exe + (a.Running ? "" : " · сейчас не запущена"), FontSize = 12, Foreground = Res("Mut") });
            g.Children.Add(name);

            var seg = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var opts = new[] { Tuple.Create(GameRules.Close, "Закрыть"), Tuple.Create(GameRules.Low, "Ниже приоритет"), Tuple.Create(GameRules.Keep, "Не трогать") };
            var cells = new List<Tuple<string, Border, TextBlock>>();
            Action paint = () =>
            {
                string cur = GameRules.Get(cfg.GameRules, a.Exe);
                foreach (var c in cells)
                {
                    bool sel = c.Item1 == cur;
                    c.Item2.Background = sel ? Res("Acc") : Res("Card");
                    c.Item2.BorderBrush = sel ? Res("Acc") : Res("Line");
                    c.Item3.Foreground = sel ? Brushes.White : Res("Txt");
                }
            };
            for (int i = 0; i < opts.Length; i++)
            {
                var o = opts[i];
                var tb = new TextBlock { Text = o.Item2, FontSize = 12.5, Margin = new Thickness(12, 5, 12, 6) };
                var b = new Border
                {
                    Child = tb, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(i == 0 ? 0 : -1, 0, 0, 0),
                    CornerRadius = i == 0 ? new CornerRadius(6, 0, 0, 6) : i == opts.Length - 1 ? new CornerRadius(0, 6, 6, 0) : new CornerRadius(0)
                };
                b.MouseLeftButtonUp += (s, e) =>
                {
                    cfg.GameRules[a.Exe] = o.Item1; cfg.Save();
                    paint();
                };
                cells.Add(Tuple.Create(o.Item1, b, tb));
                seg.Children.Add(b);
            }
            paint();
            Grid.SetColumn(seg, 1);
            g.Children.Add(seg);
            return new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 8), Child = g };
        }
    }
}
