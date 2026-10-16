using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Nomi
{
    // Prepares, in the background, a reply to each of the last N messages of the inbox.
    // Interactive requests always win: a background draft is cancelled and resumed later.
    // Nothing is ever sent; with AutoDrafts the replies are only saved in Outlook's Drafts.
    class SuggestionService
    {
        readonly LlmEngine engine;
        readonly OutlookBridge outlook;
        readonly Dispatcher ui;
        readonly DispatcherTimer timer;
        readonly Dictionary<string, int> attempts = new Dictionary<string, int>();
        Settings settings;
        bool running;
        CancellationTokenSource cancel = new CancellationTokenSource();

        public readonly ObservableCollection<Suggestion> Items = new ObservableCollection<Suggestion>();
        public event Action Changed;               // list or a status changed (UI thread)
        public event Action<Suggestion> Ready;     // a suggestion has just been written (UI thread)

        public SuggestionService(LlmEngine engine, OutlookBridge outlook, Settings settings, Dispatcher ui)
        {
            this.engine = engine;
            this.outlook = outlook;
            this.settings = settings;
            this.ui = ui;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            timer.Tick += delegate { var ignored = CycleAsync(); };
        }

        public int ReadyCount { get { return Items.Count(i => i.Status == "Prête" || i.DraftSaved); } }

        public void UpdateSettings(Settings s)
        {
            bool wasOn = settings.OutlookSuggestions;
            int oldCount = (int)settings.SuggestionCount;
            bool oldUnread = settings.SuggestionsUnreadOnly;
            settings = s.Clone();
            if (!settings.OutlookSuggestions)
            {
                timer.Stop();
                cancel.Cancel();
                cancel = new CancellationTokenSource();
                Items.Clear();
                attempts.Clear();
                Notify();
                return;
            }
            if (!timer.IsEnabled) timer.Start();
            if (!wasOn || oldCount != (int)settings.SuggestionCount || oldUnread != settings.SuggestionsUnreadOnly)
                RefreshSoon();
        }

        public void RefreshSoon()
        {
            if (!settings.OutlookSuggestions) return;
            ui.BeginInvoke(new Action(() => { var ignored = CycleAsync(); }), DispatcherPriority.Background);
        }

        async Task CycleAsync()
        {
            if (running || !settings.OutlookSuggestions || !OutlookBridge.IsRunning) return;
            running = true;
            var token = cancel.Token;
            try
            {
                List<MailItem> recent;
                try { recent = await outlook.GetRecentAsync((int)settings.SuggestionCount, settings.SuggestionsUnreadOnly); }
                catch (OutlookUnavailableException e) { Log.Write("Suggestions: " + e.Message); return; }
                if (token.IsCancellationRequested) return;
                Merge(recent);

                foreach (var item in Items.ToList())
                {
                    if (token.IsCancellationRequested || !settings.OutlookSuggestions) break;
                    if (!string.IsNullOrEmpty(item.Text) || item.Failed) continue;
                    // Let the user's own requests go first.
                    while (engine.Busy && !token.IsCancellationRequested) await Task.Delay(1500);
                    await WriteAsync(item, token);
                }
            }
            catch (Exception e) { Log.Error("Suggestions cycle", e); }
            finally { running = false; }
        }

        void Merge(List<MailItem> recent)
        {
            // Same messages in the same order: keep the list as it is (and the user's selection).
            if (recent.Select(m => m.Key).SequenceEqual(Items.Select(i => i.Mail.Key))) return;
            var byKey = Items.ToDictionary(i => i.Mail.Key);
            var next = recent.Select(m => { Suggestion s; return byKey.TryGetValue(m.Key, out s) ? s : new Suggestion { Mail = m }; }).ToList();
            Items.Clear();
            foreach (var s in next) Items.Add(s);
            foreach (var key in attempts.Keys.Where(k => next.All(s => s.Mail.Key != k)).ToList()) attempts.Remove(key);
            Notify();
        }

        async Task WriteAsync(Suggestion item, CancellationToken token)
        {
            int tries;
            attempts.TryGetValue(item.Mail.Key, out tries);
            if (tries >= 3) { item.Failed = true; item.Status = "Échec"; Notify(); return; }
            attempts[item.Mail.Key] = tries + 1;

            item.Status = "Rédaction…";
            Notify();
            try
            {
                MailItem full = item.Mail.Body != null ? item.Mail : await outlook.LoadAsync(item.Mail);
                full.StoreId = full.StoreId ?? item.Mail.StoreId;
                item.Mail = full;
                var request = Prompts.ReplyToMail(full, null, settings.DefaultTone, settings);
                string body = Prompts.CleanBody(await engine.ChatAsync(request.Messages,
                    (int)settings.MaxTokens, settings.Temperature, null, token, true));
                if (body.Length == 0) throw new InvalidOperationException("réponse vide");
                string text = request.Frame.Compose(body);
                item.Text = text;
                item.Status = "Prête";
                if (settings.AutoDrafts)
                {
                    try
                    {
                        await outlook.CreateReplyAsync(full, text, false);
                        item.DraftSaved = true;
                        item.Status = "Brouillon";
                    }
                    catch (Exception e) { Log.Write("Draft: " + e.Message); }
                }
                Notify();
                if (Ready != null) Ready(item);
            }
            catch (OperationCanceledException)
            {
                // Pre-empted by the user: try again on the next cycle, this attempt does not count.
                attempts[item.Mail.Key] = tries;
                item.Status = "En attente";
                Notify();
            }
            catch (Exception e)
            {
                Log.Write("Suggestion for '" + item.Mail.Subject + "': " + e.Message);
                item.Status = tries + 1 >= 3 ? "Échec" : "En attente";
                item.Failed = tries + 1 >= 3;
                Notify();
            }
        }

        // Called when the user has taken over a message (it is answered in the panel).
        public void MarkHandled(string key, string text)
        {
            var item = Items.FirstOrDefault(i => i.Mail.Key == key);
            if (item == null || string.IsNullOrEmpty(text)) return;
            item.Text = text;
            if (!item.DraftSaved) item.Status = "Prête";
            Notify();
        }

        void Notify() { if (Changed != null) Changed(); }

        public void Stop()
        {
            timer.Stop();
            cancel.Cancel();
        }
    }
}
