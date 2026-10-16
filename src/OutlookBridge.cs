using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nomi
{
    class MailItem
    {
        public string EntryId;
        public string StoreId;
        public string Sender;
        public string Subject;
        public string Body;          // without the quoted history, trimmed for the model
        public DateTime Received;
        public bool Unread;

        public string Key { get { return EntryId ?? ""; } }

        public string Title
        {
            get { return (string.IsNullOrEmpty(Sender) ? "" : Sender + " · ") + Subject; }
        }
    }

    class OutlookUnavailableException : Exception
    {
        public OutlookUnavailableException(string message) : base(message) { }
    }

    // Classic Outlook (2016 to Microsoft 365) through late-bound COM: no interop assembly needed.
    // Every COM call runs on one dedicated STA thread; callers get Tasks back.
    // The "new Outlook" (olk.exe) has no COM API and is not supported.
    class OutlookBridge : IDisposable
    {
        const int olFolderInbox = 6;
        const int olMail = 43;
        const int olExplorer = 34, olInspector = 35;
        const int olFormatHTML = 2;
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
        const int MaxBody = 3500;

        readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        readonly Thread thread;
        object outlook;

        public OutlookBridge()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "Outlook" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public static bool IsRunning { get { return Process.GetProcessesByName("OUTLOOK").Length > 0; } }

        void Loop()
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception e) { Log.Error("Outlook loop", e); }
            }
            outlook = null;
        }

        // Actions with side effects (creating a reply) are never retried, to avoid duplicates.
        Task<T> Run<T>(Func<T> func, bool retry = true)
        {
            var tcs = new TaskCompletionSource<T>();
            Action action = () =>
            {
                try { tcs.SetResult(retry ? WithRetry(func) : func()); }
                catch (Exception e)
                {
                    if (!(e is OutlookUnavailableException)) { outlook = null; Log.Write("Outlook: " + e.GetType().Name + ": " + e.Message); }
                    tcs.SetException(e is OutlookUnavailableException ? e : new OutlookUnavailableException(Friendly(e)));
                }
            };
            try { work.Add(action); }
            catch (InvalidOperationException) { tcs.SetException(new OutlookUnavailableException("Nomi se ferme")); }
            return tcs.Task;
        }

        // Outlook rejects calls while it is busy (a dialog is open, it is syncing...): retry a little.
        T WithRetry<T>(Func<T> func)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return func(); }
                catch (OutlookUnavailableException) { throw; }
                catch (Exception e)
                {
                    var com = e as COMException;
                    bool busy = com != null && (com.ErrorCode == RPC_E_CALL_REJECTED || com.ErrorCode == RPC_E_SERVERCALL_RETRYLATER);
                    if (busy && attempt < 8) { Thread.Sleep(400); continue; }
                    // Outlook was restarted: the cached COM object is dead. Reconnect once.
                    if (!busy && attempt == 0 && outlook != null) { outlook = null; continue; }
                    throw;
                }
            }
        }

        static string Friendly(Exception e)
        {
            var com = e as COMException;
            if (com != null && (com.ErrorCode == RPC_E_CALL_REJECTED || com.ErrorCode == RPC_E_SERVERCALL_RETRYLATER))
                return "Outlook est occupé (une fenêtre de dialogue est peut-être ouverte). Réessayez dans un instant.";
            return "Outlook n'a pas répondu : " + e.Message;
        }

        dynamic Outlook()
        {
            if (outlook != null) return outlook;
            if (!IsRunning) throw new OutlookUnavailableException("Outlook n'est pas ouvert.");
            try { outlook = Marshal.GetActiveObject("Outlook.Application"); }
            catch (COMException)
            {
                // Different integrity levels (Outlook "as administrator") make the ROT invisible.
                throw new OutlookUnavailableException("Impossible de joindre Outlook. Vérifiez qu'il n'est pas lancé en administrateur.");
            }
            return outlook;
        }

        // The message the user is looking at: the open message window if it is in front,
        // otherwise the first item selected in the main window.
        public Task<MailItem> GetSelectedMailAsync()
        {
            return Run<MailItem>(() =>
            {
                dynamic ol = Outlook();
                dynamic item = null;
                dynamic win = null;
                try { win = ol.ActiveWindow(); } catch { }
                try
                {
                    int cls = win == null ? 0 : (int)win.Class;
                    if (cls == olInspector) item = win.CurrentItem;
                    if (item == null)
                    {
                        dynamic explorer = cls == olExplorer ? win : ol.ActiveExplorer();
                        if (explorer != null)
                        {
                            dynamic selection = null;
                            try { selection = explorer.Selection; } catch { }   // some views have no selection
                            if (selection != null && (int)selection.Count > 0) item = selection.Item(1);
                            Release(selection);
                            if (explorer != win) Release(explorer);
                        }
                    }
                    if (item == null) return null;
                    if ((int)item.Class != olMail) { Release(item); return null; }
                    var mail = Describe(item, true);
                    Release(item);
                    return mail;
                }
                finally { Release(win); }
            });
        }

        // Most recent messages of the default inbox, newest first (identifiers only: cheap to poll).
        public Task<List<MailItem>> GetRecentAsync(int count, bool unreadOnly)
        {
            return Run(() =>
            {
                var result = new List<MailItem>();
                dynamic ns = Outlook().GetNamespace("MAPI");
                dynamic inbox = ns.GetDefaultFolder(olFolderInbox);
                dynamic all = inbox.Items;
                dynamic items = unreadOnly ? all.Restrict("[UnRead] = True") : all;
                string storeId = inbox.StoreID;
                try
                {
                    items.Sort("[ReceivedTime]", true);
                    dynamic item = items.GetFirst();
                    for (int scanned = 0; item != null && result.Count < count && scanned < 60; scanned++)
                    {
                        try
                        {
                            if ((int)item.Class == olMail)
                            {
                                var m = Describe(item, false);
                                m.StoreId = storeId;
                                result.Add(m);
                            }
                        }
                        finally { Release(item); }
                        item = items.GetNext();
                    }
                    Release(item);
                }
                finally
                {
                    if (!ReferenceEquals(items, all)) Release(items);
                    Release(all);
                    Release(inbox);
                    Release(ns);
                }
                return result;
            });
        }

        // Fills in the body of a message returned by GetRecentAsync.
        public Task<MailItem> LoadAsync(MailItem m)
        {
            return Run<MailItem>(() =>
            {
                dynamic ns = Outlook().GetNamespace("MAPI");
                dynamic item = ns.GetItemFromID(m.EntryId, m.StoreId);
                try { return Describe(item, true); }
                finally { Release(item); Release(ns); }
            });
        }

        // Opens (or, with display = false, silently saves to Drafts) a reply with the text on top.
        public Task<bool> CreateReplyAsync(MailItem target, string text, bool display)
        {
            return Run(() =>
            {
                dynamic ns = Outlook().GetNamespace("MAPI");
                dynamic item = string.IsNullOrEmpty(target.StoreId) ? ns.GetItemFromID(target.EntryId) : ns.GetItemFromID(target.EntryId, target.StoreId);
                dynamic reply = item.Reply();
                try
                {
                    // The reply signature is added when the message window is created, so create it first
                    // and insert the text afterwards; otherwise it would end up below the signature.
                    if (display) reply.Display(false);
                    else { dynamic inspector = reply.GetInspector; Release(inspector); }

                    if ((int)reply.BodyFormat == olFormatHTML) reply.HTMLBody = InsertHtml(Convert.ToString(reply.HTMLBody), text);
                    else reply.Body = text + "\r\n\r\n" + Convert.ToString(reply.Body);

                    if (!display) { reply.Save(); reply.Close(0 /* olSave */); }
                    return true;
                }
                finally
                {
                    Release(reply);
                    Release(item);
                    Release(ns);
                    if (display) Native.BringProcessToFront("OUTLOOK");
                }
            }, false);
        }

        static string InsertHtml(string html, string text)
        {
            var sb = new StringBuilder();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                sb.Append("<p class=MsoNormal>").Append(line.Length == 0 ? "&nbsp;" : WebUtility.HtmlEncode(line)).Append("</p>");
            }
            sb.Append("<p class=MsoNormal>&nbsp;</p>");
            string block = sb.ToString();

            if (string.IsNullOrEmpty(html)) return "<html><body>" + block + "</body></html>";
            var body = Regex.Match(html, "<body[^>]*>", RegexOptions.IgnoreCase);
            if (!body.Success) return block + html;
            // Word wraps the content in <div class=WordSection1>: stay inside it to keep the user's fonts.
            var section = Regex.Match(html.Substring(body.Index + body.Length), "^\\s*<div[^>]*WordSection1[^>]*>", RegexOptions.IgnoreCase);
            int at = body.Index + body.Length + (section.Success ? section.Length : 0);
            return html.Insert(at, block);
        }

        static MailItem Describe(dynamic item, bool withBody)
        {
            var m = new MailItem();
            m.EntryId = Convert.ToString(item.EntryID);
            try { m.StoreId = Convert.ToString(item.Parent.StoreID); } catch { }
            m.Sender = (Convert.ToString(item.SenderName) ?? "").Trim();
            string subject = (Convert.ToString(item.Subject) ?? "").Trim();
            m.Subject = subject.Length == 0 ? "(Sans objet)" : subject;
            try { m.Received = item.ReceivedTime; } catch { m.Received = DateTime.MinValue; }
            try { m.Unread = item.UnRead; } catch { }
            if (withBody)
            {
                string body = "";
                try { body = Convert.ToString(item.Body) ?? ""; } catch (Exception e) { Log.Write("Body: " + e.Message); }
                m.Body = Prompts.CleanMailBody(body, MaxBody);
            }
            return m;
        }

        static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        public void Dispose()
        {
            if (!work.IsAddingCompleted) work.CompleteAdding();
        }
    }
}
