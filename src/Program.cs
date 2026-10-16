using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("Nomi")]
[assembly: AssemblyProduct("Nomi")]
[assembly: AssemblyCompany("Nomi")]
[assembly: AssemblyDescription("Assistant de rédaction local pour Outlook")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace Nomi
{
    static class Program
    {
        const string InstanceMutex = @"Local\Nomi.Instance";
        const string QuitEvent = @"Local\Nomi.Quit";
        const string ShowEvent = @"Local\Nomi.Show";

        [STAThread]
        static int Main(string[] args)
        {
            Func<string, bool> has = flag => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("Task: " + e.Exception); e.SetObserved(); };

            if (has("--quit")) { StopRunningInstances(); return 0; }

            var settings = Settings.Load();
            if (has("--autostart") && !settings.LaunchAtStartup) return 0;

            bool created;
            using (var mutex = new Mutex(true, InstanceMutex, out created))
            {
                if (!created)
                {
                    // Already running: bring it forward instead of starting a second copy.
                    Signal(ShowEvent);
                    return 0;
                }
                try { new Controller(settings, has("--settings")).Run(); }
                catch (Exception e) { Log.Error("Fatal", e); return 1; }
            }
            return 0;
        }

        static void Signal(string name)
        {
            try
            {
                EventWaitHandle h;
                if (EventWaitHandle.TryOpenExisting(name, out h)) using (h) h.Set();
            }
            catch { }
        }

        // Used by the installer before uninstalling or upgrading: ask politely, then force.
        // The model server dies with Nomi (job object), and is also stopped here just in case.
        static void StopRunningInstances()
        {
            Signal(QuitEvent);
            int self = Process.GetCurrentProcess().Id;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName("Nomi").Any(p => p.Id != self)) Thread.Sleep(150);
            foreach (var p in Process.GetProcessesByName("Nomi"))
                if (p.Id != self) try { p.Kill(); } catch { }
            string engine = System.IO.Path.Combine(LlmEngine.AppDir, "engine", "llama-server.exe");
            foreach (var p in Process.GetProcessesByName("llama-server"))
            {
                try { if (string.Equals(p.MainModule.FileName, engine, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
            }
        }

        // ======================================================================================

        class Controller
        {
            Settings settings;
            readonly bool openSettings;
            Application app;
            MascotWindow mascot;
            AssistantPanel panel;
            SettingsWindow settingsWindow;
            LlmEngine engine;
            OutlookBridge outlook;
            SuggestionService suggestions;
            ZoneSelector zones;
            WinForms.NotifyIcon tray;
            WinForms.ToolStripMenuItem toggleMascotItem;
            System.Drawing.Icon appIcon;
            DispatcherTimer saveTimer;

            // What the bubble is answering.
            MailItem targetMail;        // the message the reply goes to (if any)
            string zoneText;            // text read in a highlighted zone (manual mode)
            string lastAutoKey;         // message last answered automatically on a mascot click
            CancellationTokenSource generation;

            public Controller(Settings settings, bool openSettings)
            {
                this.settings = settings;
                this.openSettings = openSettings;
            }

            public void Run()
            {
                WinForms.Application.EnableVisualStyles();
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log.Error("UI", e.Exception); e.Handled = true; };
                app.SessionEnding += delegate { app.Shutdown(); };
                Ui.InstallAppResources(app);
                appIcon = LoadIcon();

                engine = new LlmEngine(settings);
                engine.StateChanged += () => app.Dispatcher.BeginInvoke(new Action(OnEngineState));
                outlook = new OutlookBridge();
                zones = new ZoneSelector(settings);

                mascot = new MascotWindow(settings);
                mascot.Clicked += OnMascotClicked;
                mascot.MenuCommand += OnMenuCommand;
                mascot.PlacementChanged += delegate { OnPlacementEdited(mascot.Settings); };
                mascot.PositionMoved += delegate { if (panel.IsVisible && CurrentPlacement().PanelPlacement == "mascot") panel.PlaceNear(Native.Bounds(mascot)); };
                mascot.HotkeyPanelPressed += delegate { TogglePanel(PanelMode.Ask); };
                mascot.HotkeyZonePressed += delegate { var ignored = ReplyToZoneAsync(); };

                panel = new AssistantPanel(settings);
                WirePanel();

                suggestions = new SuggestionService(engine, outlook, settings, app.Dispatcher);
                suggestions.Changed += OnSuggestionsChanged;
                suggestions.Ready += delegate { if (!panel.IsVisible) mascot.View.Pulse(MascotView.Success, 1100); };
                panel.SetSuggestions(suggestions.Items);

                CreateTray();
                WatchSignals();

                saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                saveTimer.Tick += delegate { saveTimer.Stop(); settings.Save(); };

                if (settings.ShowMascot) ShowMascot(true);
                Startup.Apply(settings.LaunchAtStartup);
                suggestions.UpdateSettings(settings);
                if (settings.PreloadModel) engine.Preload();
                OnEngineState();
                if (openSettings) app.Dispatcher.BeginInvoke(new Action(OpenSettings), DispatcherPriority.ApplicationIdle);

                app.Run();

                // Shutdown: stop everything that runs in the background.
                if (generation != null) generation.Cancel();
                suggestions.Stop();
                engine.Dispose();
                outlook.Dispose();
                settings.Save();
                tray.Visible = false;
                tray.Dispose();
            }

            Settings CurrentPlacement() { return settingsWindow != null ? settingsWindow.Working : settings; }

            // ------------------------------------------------------------ mascot & panel

            void ShowMascot(bool greet)
            {
                if (!mascot.IsVisible)
                {
                    mascot.Show();
                    mascot.PlaceInitially();
                }
                if (greet) mascot.View.Pulse(MascotView.Greeting, 1000);
                if (toggleMascotItem != null) toggleMascotItem.Text = "Masquer la mascotte";
            }

            void HideMascot()
            {
                mascot.Hide();
                if (toggleMascotItem != null) toggleMascotItem.Text = "Afficher la mascotte";
            }

            // One click on Nomi: open the bubble on the message selected in Outlook and start
            // drafting right away. Clicking again closes the bubble.
            void OnMascotClicked()
            {
                if (panel.IsVisible && settingsWindow == null) { ClosePanel(); return; }
                OpenPanel(PanelMode.Reply);
                var ignored = TargetSelectedMailAsync(true);
            }

            void OnMenuCommand(string command)
            {
                switch (command)
                {
                    case "reply": OpenPanel(PanelMode.Reply); var a = TargetSelectedMailAsync(true, true); break;
                    case "zone": var b = ReplyToZoneAsync(); break;
                    case "suggestions": OpenPanel(PanelMode.Suggestions); suggestions.RefreshSoon(); break;
                    case "ask": OpenPanel(PanelMode.Ask); panel.FocusInput(); break;
                    case "settings": OpenSettings(); break;
                    case "hide": HideMascot(); ShowTrayHint(); break;
                }
            }

            void TogglePanel(PanelMode mode)
            {
                if (panel.IsVisible && panel.Mode == mode && settingsWindow == null) { ClosePanel(); return; }
                OpenPanel(mode);
                panel.FocusInput();
            }

            void OpenPanel(PanelMode mode)
            {
                panel.Mode = mode;
                if (!panel.IsVisible)
                {
                    panel.Show();
                    if (mascot.IsVisible) panel.PlaceNear(Native.Bounds(mascot));
                    else PlacePanelAlone();
                }
                panel.Activate();
                mascot.Touch();
                if (!panel.Busy && mascot.View.Activity == MascotView.Idle) mascot.View.SetActivity(MascotView.Listen);
            }

            void PlacePanelAlone()
            {
                var work = WinForms.Screen.PrimaryScreen.WorkingArea;
                var b = Native.Bounds(panel);
                if (CurrentPlacement().PanelPlacement == "free") { panel.PlaceNear(new Int32Rect(work.Right, work.Bottom, 0, 0)); return; }
                Native.MoveTo(panel, work.Right - b.Width - 16, work.Bottom - b.Height - 16);
            }

            void ClosePanel()
            {
                if (settingsWindow != null) return;   // the bubble stays as a preview while settings are open
                panel.Hide();
                zones.ReleaseHighlight();
                if (!panel.Busy) mascot.View.SetActivity(MascotView.Idle);
            }

            void WirePanel()
            {
                panel.CloseRequested += ClosePanel;
                panel.UseSelectionRequested += delegate { var t = TargetSelectedMailAsync(false, true); };
                panel.ZoneRequested += delegate { var t = ReplyToZoneAsync(); };
                panel.GenerateRequested += delegate { var t = GenerateReplyAsync(); };
                panel.RegenerateRequested += delegate { var t = panel.Mode == PanelMode.Ask ? AskAsync() : GenerateReplyAsync(); };
                panel.AskRequested += delegate { var t = AskAsync(); };
                panel.StopRequested += delegate { if (generation != null) generation.Cancel(); };
                panel.InsertRequested += delegate { var t = InsertAsync(); };
                panel.EnableSuggestionsRequested += delegate
                {
                    settings.OutlookSuggestions = true;
                    settings.Save();
                    if (settingsWindow != null) { settingsWindow.Working.OutlookSuggestions = true; }
                    panel.ApplySettings(CurrentPlacement());
                    suggestions.UpdateSettings(settings);
                };
                panel.SuggestionPicked += OnSuggestionPicked;
                panel.LayoutEdited += delegate
                {
                    OnPlacementEdited(panel.Settings);
                    if (CurrentPlacement().PanelPlacement == "mascot" && mascot.IsVisible) panel.PlaceNear(Native.Bounds(mascot));
                };
                panel.ModeChanged += mode => { if (mode == PanelMode.Suggestions) suggestions.RefreshSoon(); };
            }

            // Position and size changed by hand are saved at once, settings window open or not.
            void OnPlacementEdited(Settings from)
            {
                foreach (var target in new[] { settings, mascot.Settings, panel.Settings })
                {
                    if (ReferenceEquals(target, from)) continue;
                    target.MascotX = from.MascotX; target.MascotY = from.MascotY; target.MascotSize = from.MascotSize;
                    target.PanelX = from.PanelX; target.PanelY = from.PanelY; target.PanelPlacement = from.PanelPlacement;
                    target.PanelWidth = from.PanelWidth; target.PanelHeight = from.PanelHeight;
                }
                if (settingsWindow != null) settingsWindow.SyncPlacement(from);
                saveTimer.Stop();
                saveTimer.Start();
            }

            // ------------------------------------------------------------ targets

            async Task TargetSelectedMailAsync(bool autoGenerate, bool force = false)
            {
                if (panel.Busy && !force) return;
                zoneText = null;
                zones.ReleaseHighlight();
                if (!OutlookBridge.IsRunning)
                {
                    SetTarget(null, "Outlook n'est pas ouvert. Vous pouvez surligner une zone de l'écran ou poser une question.");
                    return;
                }
                panel.SetStatus("Lecture du mail sélectionné…", false);
                MailItem mail;
                try { mail = await outlook.GetSelectedMailAsync(); }
                catch (OutlookUnavailableException e)
                {
                    SetTarget(null, e.Message);
                    panel.SetStatus(e.Message, true);
                    return;
                }
                panel.SetStatus("", false);
                if (mail == null)
                {
                    SetTarget(null, "Sélectionnez un mail dans Outlook puis cliquez sur « Mail sélectionné », ou surlignez une zone de l'écran.");
                    return;
                }
                bool changed = targetMail == null || targetMail.Key != mail.Key;
                SetTarget(mail, null);

                // A suggestion already written for this message: show it instead of writing again.
                var ready = suggestions.Items.FirstOrDefault(s => s.Mail.Key == mail.Key && !string.IsNullOrEmpty(s.Text));
                if (ready != null && (changed || panel.Output.Length == 0))
                {
                    panel.Output = ready.Text;
                    panel.SetStatus("Suggestion préparée à l'avance · « Autre version » pour en écrire une nouvelle.", false);
                    lastAutoKey = mail.Key;
                    return;
                }
                if (autoGenerate && (changed || lastAutoKey != mail.Key || panel.Output.Length == 0))
                {
                    lastAutoKey = mail.Key;
                    await GenerateReplyAsync();
                }
                else if (changed) panel.Output = "";
            }

            void SetTarget(MailItem mail, string emptyMessage)
            {
                targetMail = mail;
                panel.SetTarget(mail, emptyMessage);
                panel.CanInsert = mail != null;
                panel.Refresh();
            }

            async Task ReplyToZoneAsync()
            {
                if (zones.Active) return;
                bool panelWasVisible = panel.IsVisible;
                if (panelWasVisible) panel.Hide();          // so it does not cover what the user wants to select
                bool mascotWasVisible = mascot.IsVisible;
                if (mascotWasVisible) mascot.Hide();
                ZoneResult zone;
                try { zone = await zones.PickAsync(); }
                finally
                {
                    if (mascotWasVisible) mascot.Show();
                }
                if (zone == null)
                {
                    if (panelWasVisible) OpenPanel(panel.Mode);
                    return;
                }

                OpenPanel(PanelMode.Reply);
                mascot.View.SetActivity(MascotView.Think);
                panel.SetBusy(true, "Lecture de la zone surlignée…");
                string text;
                try { using (zone.Image) text = await ScreenOcr.RecognizeAsync(zone.Image); }
                catch (Exception e)
                {
                    Log.Error("OCR", e);
                    panel.SetBusy(false, "Lecture impossible : " + e.Message);
                    mascot.View.SetActivity(MascotView.Listen);
                    mascot.View.Pulse(MascotView.Error);
                    zones.ReleaseHighlight();
                    return;
                }
                panel.SetBusy(false, "");
                if (string.IsNullOrWhiteSpace(text))
                {
                    panel.SetStatus("Aucun texte reconnu dans cette zone. Essayez une zone un peu plus grande.", true);
                    mascot.View.SetActivity(MascotView.Listen);
                    mascot.View.Pulse(MascotView.Error);
                    zones.ReleaseHighlight();
                    return;
                }

                // Over Outlook: the reply can go to the message currently selected there.
                MailItem context = null;
                if (string.Equals(zone.ProcessName, "OUTLOOK", StringComparison.OrdinalIgnoreCase))
                {
                    try { context = await outlook.GetSelectedMailAsync(); } catch (OutlookUnavailableException) { }
                }
                targetMail = context;
                zoneText = text;
                panel.SetZoneTarget(text, context);
                panel.CanInsert = context != null;
                panel.Refresh();
                await GenerateReplyAsync();
            }

            void OnSuggestionPicked(Suggestion s)
            {
                if (panel.Busy) return;
                zoneText = null;
                SetTarget(s.Mail, null);
                if (!string.IsNullOrEmpty(s.Text))
                {
                    panel.Output = s.Text;
                    panel.SetStatus(s.DraftSaved ? "Déjà enregistrée dans les Brouillons d'Outlook." : "Suggestion prête.", false);
                }
                else
                {
                    panel.Output = "";
                    var t = GenerateReplyAsync();
                }
            }

            // ------------------------------------------------------------ generation

            async Task GenerateReplyAsync()
            {
                if (zoneText == null && targetMail == null)
                {
                    panel.SetStatus("Choisissez d'abord un mail (bouton « Mail sélectionné ») ou une zone de l'écran.", true);
                    mascot.View.Pulse(MascotView.Surprise, 700);
                    return;
                }
                MailItem mail = targetMail;
                if (zoneText == null && mail.Body == null)
                {
                    try { mail = await outlook.LoadAsync(mail); mail.StoreId = mail.StoreId ?? targetMail.StoreId; targetMail = mail; }
                    catch (OutlookUnavailableException e) { panel.SetStatus(e.Message, true); return; }
                }
                var s = CurrentPlacement();
                var request = zoneText != null
                    ? Prompts.ReplyToExcerpt(zoneText, mail, panel.Instruction, panel.Tone ?? s.DefaultTone, s)
                    : Prompts.ReplyToMail(mail, panel.Instruction, panel.Tone ?? s.DefaultTone, s);
                string key = mail != null ? mail.Key : null;
                string result = await RunAsync(request.Messages, request.Frame);
                if (result != null && key != null && zoneText == null) suggestions.MarkHandled(key, result);
                if (zoneText != null) zones.ReleaseHighlight();
            }

            async Task AskAsync()
            {
                string q = panel.Question.Trim();
                if (q.Length == 0) { panel.SetStatus("Écrivez d'abord votre question.", true); return; }
                await RunAsync(Prompts.FreeQuestion(q, null), null);
            }

            // Streams an answer into the bubble while the mascot thinks, then speaks. Replies come
            // with a frame: greeting, closing and signature are added around what the model writes.
            async Task<string> RunAsync(System.Collections.Generic.List<ChatMessage> messages, ReplyFrame frame)
            {
                bool isReply = frame != null;
                if (generation != null) generation.Cancel();
                var cts = new CancellationTokenSource();
                generation = cts;
                var s = CurrentPlacement();

                panel.Output = isReply ? frame.Opening : "";
                bool cold = engine.State != EngineState.Ready;
                panel.SetBusy(true, cold ? "Chargement du modèle (une seule fois)…" : "Nomi réfléchit…");
                mascot.View.SetActivity(cold ? MascotView.Loading : MascotView.Think);
                bool first = true;
                try
                {
                    string text = await engine.ChatAsync(messages, (int)s.MaxTokens, s.Temperature, chunk =>
                    {
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (cts.IsCancellationRequested || generation != cts) return;
                            if (first)
                            {
                                first = false;
                                panel.SetStatus("Rédaction…", false);
                                mascot.View.SetActivity(MascotView.Speak);
                            }
                            panel.Append(chunk);
                        }));
                    }, cts.Token, false);

                    if (generation != cts) return null;
                    if (isReply)
                    {
                        string body = Prompts.CleanBody(text);
                        text = body.Length == 0 ? "" : frame.Compose(body);
                    }
                    else text = text.Trim();
                    panel.Output = text;
                    panel.SetBusy(false, text.Length == 0 ? "Le modèle n'a rien répondu, réessayez." : isReply
                        ? "Réponse prête · modifiez-la librement avant de l'utiliser."
                        : "Réponse prête.");
                    mascot.View.SetActivity(panel.IsVisible ? MascotView.Listen : MascotView.Idle);
                    mascot.View.Pulse(text.Length == 0 ? MascotView.Error : MascotView.Success);
                    return text;
                }
                catch (OperationCanceledException)
                {
                    if (generation == cts) { panel.SetBusy(false, "Arrêté."); mascot.View.SetActivity(MascotView.Listen); }
                    return null;
                }
                catch (Exception e)
                {
                    if (e is System.Net.WebException && cts.IsCancellationRequested)
                    {
                        if (generation == cts) { panel.SetBusy(false, "Arrêté."); mascot.View.SetActivity(MascotView.Listen); }
                        return null;
                    }
                    Log.Error("Generation", e);
                    if (generation == cts)
                    {
                        panel.SetBusy(false, e.Message);
                        panel.SetStatus(e.Message, true);
                        mascot.View.SetActivity(MascotView.Listen);
                        mascot.View.Pulse(MascotView.Error);
                    }
                    return null;
                }
                finally
                {
                    if (generation == cts) generation = null;
                    cts.Dispose();
                }
            }

            async Task InsertAsync()
            {
                string text = panel.Mode == PanelMode.Ask ? panel.Output : Prompts.CleanAnswer(panel.Output);
                if (text.Length == 0) return;
                if (panel.Mode == PanelMode.Ask || targetMail == null)
                {
                    AssistantPanel.CopyToClipboard(text);
                    panel.SetStatus("Copié dans le presse-papiers.", false);
                    return;
                }
                panel.SetStatus("Ouverture de la réponse dans Outlook…", false);
                try
                {
                    Native.AllowSetForegroundWindow(-1);
                    await outlook.CreateReplyAsync(targetMail, text, true);
                    panel.SetStatus("Réponse ouverte dans Outlook : relisez-la puis envoyez-la.", false);
                    suggestions.MarkHandled(targetMail.Key, text);
                    mascot.View.Pulse(MascotView.Success);
                    zones.ReleaseHighlight();
                }
                catch (OutlookUnavailableException e)
                {
                    AssistantPanel.CopyToClipboard(text);
                    panel.SetStatus(e.Message + " Le texte a été copié dans le presse-papiers.", true);
                    mascot.View.Pulse(MascotView.Error);
                }
            }

            // ------------------------------------------------------------ engine & suggestions

            void OnEngineState()
            {
                string text = engine.StatusText;
                panel.SetEngine(engine.State, text);
                if (settingsWindow != null) settingsWindow.SetEngineStatus(text);
                UpdateTrayText();
                if (engine.State == EngineState.Starting && panel.Busy) mascot.View.SetActivity(MascotView.Loading);
            }

            void OnSuggestionsChanged()
            {
                panel.SetSuggestions(suggestions.Items);
                mascot.SetBadge(CurrentPlacement().OutlookSuggestions ? suggestions.ReadyCount : 0);
                UpdateTrayText();
            }

            void UpdateTrayText()
            {
                if (tray == null) return;
                string t = "Nomi · " + engine.StatusText;
                if (settings.OutlookSuggestions && suggestions != null && suggestions.ReadyCount > 0)
                    t = "Nomi · " + suggestions.ReadyCount + " suggestion(s) prête(s)";
                tray.Text = t.Length > 63 ? t.Substring(0, 62) + "…" : t;
            }

            // ------------------------------------------------------------ settings

            void OpenSettings()
            {
                if (settingsWindow != null)
                {
                    if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
                    settingsWindow.Activate();
                    return;
                }
                settingsWindow = new SettingsWindow(settings, appIcon);
                settingsWindow.SetEngineStatus(engine.StatusText);
                settingsWindow.Preview += ApplyPreview;
                settingsWindow.CornerRequested += MoveMascotToCorner;
                settingsWindow.Saved += s =>
                {
                    settings = s.Clone();
                    settings.Save();
                    Startup.Apply(settings.LaunchAtStartup);
                    engine.ResetFailure();
                    engine.UpdateSettings(settings);
                    suggestions.UpdateSettings(settings);
                    zones.Preview(settings);
                };
                settingsWindow.Closed += delegate
                {
                    settingsWindow = null;
                    ApplyPreview(settings.Clone());
                    if (!settings.ShowMascot) HideMascot();
                    if (string.IsNullOrEmpty(panel.Output) && targetMail == null) panel.Hide();
                };

                // The bubble is shown as a live preview while the settings are open.
                ApplyPreview(settingsWindow.Working.Clone());
                OpenPanel(panel.Mode);
                settingsWindow.Show();
                settingsWindow.Activate();
            }

            void ApplyPreview(Settings s)
            {
                mascot.ApplySettings(s);
                panel.ApplySettings(s);
                panel.SetSuggestions(suggestions.Items);
                if (s.ShowMascot) ShowMascot(false); else HideMascot();
                zones.Preview(s);
                if (panel.IsVisible)
                {
                    if (s.PanelPlacement == "mascot" && mascot.IsVisible) panel.PlaceNear(Native.Bounds(mascot));
                    else if (s.PanelPlacement == "free") panel.PlaceNear(Native.Bounds(mascot));
                }
            }

            void MoveMascotToCorner(string corner)
            {
                if (!mascot.IsVisible) ShowMascot(false);
                var b = Native.Bounds(mascot);
                var work = WinForms.Screen.FromRectangle(new System.Drawing.Rectangle(b.X, b.Y, b.Width, b.Height)).WorkingArea;
                int m = (int)(24 * Native.Scale(mascot));
                int x = corner.EndsWith("R") ? work.Right - b.Width - m : work.Left + m;
                int y = corner.StartsWith("B") ? work.Bottom - b.Height - m : work.Top + m;
                Native.MoveTo(mascot, x, y);
                mascot.CommitPosition();
                if (panel.IsVisible && CurrentPlacement().PanelPlacement == "mascot") panel.PlaceNear(Native.Bounds(mascot));
            }

            // ------------------------------------------------------------ tray

            void CreateTray()
            {
                var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
                menu.Items.Add("Ouvrir Nomi", null, delegate { OpenPanel(PanelMode.Reply); });
                menu.Items.Add("Répondre au mail sélectionné", null, delegate { OnMenuCommand("reply"); });
                menu.Items.Add("Répondre à une zone de l'écran…", null, delegate { OnMenuCommand("zone"); });
                menu.Items.Add("Suggestions Outlook", null, delegate { OnMenuCommand("suggestions"); });
                menu.Items.Add(new WinForms.ToolStripSeparator());
                toggleMascotItem = new WinForms.ToolStripMenuItem(settings.ShowMascot ? "Masquer la mascotte" : "Afficher la mascotte");
                toggleMascotItem.Click += delegate
                {
                    if (mascot.IsVisible) HideMascot(); else ShowMascot(true);
                    settings.ShowMascot = mascot.IsVisible;
                    settings.Save();
                };
                menu.Items.Add(toggleMascotItem);
                menu.Items.Add("Paramètres…", null, delegate { OpenSettings(); });
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("Quitter Nomi", null, delegate { app.Shutdown(); });

                tray = new WinForms.NotifyIcon { ContextMenuStrip = menu, Text = "Nomi", Icon = appIcon, Visible = true };
                tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenSettings(); };
            }

            void ShowTrayHint()
            {
                try { tray.ShowBalloonTip(4000, "Nomi est toujours là", "Retrouvez-la dans les icônes cachées de la barre des tâches.", WinForms.ToolTipIcon.None); }
                catch { }
            }

            static System.Drawing.Icon LoadIcon()
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nomi.app.ico"))
                        return new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
                }
                catch (Exception e) { Log.Error("Icon", e); return System.Drawing.SystemIcons.Application; }
            }

            // The installer asks a running Nomi to quit; a second launch asks it to show itself.
            void WatchSignals()
            {
                try
                {
                    var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
                    ThreadPool.RegisterWaitForSingleObject(quit, delegate { app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown())); }, null, -1, true);
                    var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
                    ThreadPool.RegisterWaitForSingleObject(show, delegate
                    {
                        app.Dispatcher.BeginInvoke(new Action(() => { ShowMascot(true); OpenPanel(PanelMode.Reply); }));
                    }, null, -1, false);
                }
                catch (Exception e) { Log.Write("Signals unavailable: " + e.Message); }
            }
        }
    }
}
