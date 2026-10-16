using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nomi
{
    // How a reply opens and closes. Built by code, not by the model: small local models mix up
    // who writes to whom, so the greeting (sender's first name), tu/vous, closing and signature
    // are decided here and the model only writes the body.
    class ReplyFrame
    {
        public bool English;
        public bool Familiar;        // "tu" rather than "vous"
        public string Greeting;      // "Bonjour Camille,"
        public string Closing;       // "Cordialement,"
        public string Signature;     // may be empty

        public string Opening { get { return Greeting + "\r\n\r\n"; } }

        public string Compose(string body)
        {
            var sb = new StringBuilder();
            sb.Append(Greeting).Append("\r\n\r\n").Append(body.Trim()).Append("\r\n\r\n").Append(Closing);
            if (!string.IsNullOrWhiteSpace(Signature)) sb.Append("\r\n").Append(Signature.Trim());
            return sb.ToString();
        }
    }

    class ReplyRequest
    {
        public List<ChatMessage> Messages;
        public ReplyFrame Frame;
    }

    // Everything that is said to the model lives here.
    static class Prompts
    {
        public static readonly string[] Tones = { "neutre", "formel", "chaleureux", "bref" };

        public static string ToneLabel(string tone)
        {
            switch (tone)
            {
                case "formel": return "Formel";
                case "chaleureux": return "Chaleureux";
                case "bref": return "Bref";
                default: return "Neutre";
            }
        }

        static string ToneRule(string tone)
        {
            switch (tone)
            {
                case "formel": return "Ton formel et soutenu.";
                case "chaleureux": return "Ton cordial et chaleureux, tout en restant professionnel.";
                case "bref": return "Très court : deux ou trois phrases au maximum, sans détour.";
                default: return "Ton professionnel, simple et naturel. Phrases courtes.";
            }
        }

        // ------------------------------------------------------------------ framing

        static readonly Regex FamiliarWords = new Regex(@"\b(tu|te|toi|ton|ta|tes|t'|peux-tu|as-tu|es-tu|serais-tu)\b", RegexOptions.IgnoreCase);
        static readonly Regex FormalWords = new Regex(@"\b(vous|votre|vos|pourriez|seriez|veuillez)\b", RegexOptions.IgnoreCase);
        static readonly Regex EnglishWords = new Regex(@"\b(the|and|you|your|please|thanks|could|would|will|with|for|regards|hi|hello)\b", RegexOptions.IgnoreCase);
        static readonly Regex FrenchWords = new Regex(@"\b(le|la|les|et|vous|tu|merci|pour|avec|bonjour|est|une|des|pas)\b", RegexOptions.IgnoreCase);

        public static ReplyFrame Frame(string senderName, string text, string tone, Settings s)
        {
            text = text ?? "";
            var f = new ReplyFrame();
            f.English = s.ReplyLanguage == "en" ||
                        (s.ReplyLanguage == "auto" && EnglishWords.Matches(text).Count > FrenchWords.Matches(text).Count + 1);
            f.Familiar = !f.English && tone != "formel" && FamiliarWords.Matches(text).Count > FormalWords.Matches(text).Count;
            f.Signature = (s.SignatureName ?? "").Trim();

            string first = FirstName(senderName);
            bool collective = Regex.IsMatch(text, @"^\s*Madame,?\s*Monsieur", RegexOptions.IgnoreCase);
            if (f.English) f.Greeting = first != null ? (tone == "formel" ? "Dear " : "Hello ") + first + "," : "Hello,";
            else if (collective || (first == null && tone == "formel")) f.Greeting = "Madame, Monsieur,";
            else f.Greeting = first != null ? "Bonjour " + first + "," : "Bonjour,";

            if (f.English) f.Closing = tone == "formel" ? "Kind regards," : tone == "chaleureux" ? "All the best," : "Best regards,";
            else if (f.Familiar) f.Closing = tone == "chaleureux" ? "Belle journée," : "À bientôt,";
            else f.Closing = tone == "formel" ? "Bien cordialement," : tone == "chaleureux" ? "Belle journée," : "Cordialement,";
            return f;
        }

        // "Camille Laurent" -> Camille, "Laurent, Camille" -> Camille, "LAURENT Camille" -> Camille.
        // Services, mailing lists and addresses get no name.
        public static string FirstName(string sender)
        {
            if (string.IsNullOrWhiteSpace(sender)) return null;
            string s = sender.Trim().Trim('"', '\'');
            if (s.Contains("@") || Regex.IsMatch(s, @"\d|service|support|équipe|team|noreply|no-reply|contact|info|admin|direction|achats|newsletter|notification|SA\b|SAS\b|SARL\b|Inc\b|Ltd\b", RegexOptions.IgnoreCase))
                return null;
            if (s.Contains(",")) s = s.Substring(s.IndexOf(',') + 1).Trim();
            s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", "").Trim();
            var words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > 4) return null;
            string pick = words.FirstOrDefault(w => w.Length > 1 && !(w == w.ToUpperInvariant() && w.Length > 2) && char.IsLetter(w[0])) ?? words[0];
            if (!char.IsLetter(pick[0])) return null;
            return char.ToUpperInvariant(pick[0]) + (pick.Length > 1 && pick == pick.ToUpperInvariant() ? pick.Substring(1).ToLowerInvariant() : pick.Substring(1));
        }

        // ------------------------------------------------------------------ requests

        static string SystemForReply(ReplyFrame f, string tone)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tu rédiges le corps d'une réponse à un e-mail, à la première personne, à la place de l'utilisateur.");
            sb.AppendLine("La formule d'appel, la formule de politesse et la signature sont ajoutées automatiquement.");
            sb.AppendLine("Règles :");
            sb.AppendLine("- Écris seulement les phrases du milieu : ni « Bonjour », ni « Cordialement », ni signature, ni objet.");
            sb.AppendLine("- Pas de Markdown, pas de guillemets, pas de commentaire sur ta réponse.");
            sb.AppendLine(f.English ? "- Write in English." : "- Écris en français.");
            if (!f.English) sb.AppendLine(f.Familiar ? "- Tutoie ton interlocuteur." : "- Vouvoie ton interlocuteur.");
            sb.AppendLine("- Réponds aux questions posées, dans l'ordre. Suis la consigne de l'utilisateur si elle existe.");
            sb.AppendLine("- N'invente aucun fait : si une information nécessaire (date, délai, prix, quantité, nom) n'est ni dans le message ni dans la consigne, écris [à compléter] à sa place.");
            sb.AppendLine("- Ne dis jamais que tu es un assistant ou une IA.");
            sb.Append("- " + ToneRule(tone));
            return sb.ToString();
        }

        // One worked example, in the same language and register as the real reply: small models
        // follow an example much better than a list of rules.
        static IEnumerable<ChatMessage> Example(ReplyFrame f)
        {
            string mail, answer;
            if (f.English)
            {
                mail = "Message received from Alex Morgan, subject: Training session\n\"\"\"\nHi, could we schedule the training session next week? What would it cost for 10 people?\n\"\"\"\n\nMy instruction for this reply: accept, suggest Tuesday afternoon\n\nWrite the body of the reply I send to Alex.";
                answer = "Thanks for your message. Next week works for me: I suggest Tuesday afternoon, from 2 pm if that suits you.\n\nFor 10 people, the cost would be [à compléter]. I will send you the details once the date is confirmed.";
            }
            else if (f.Familiar)
            {
                mail = "Message reçu de Léa Bernard, objet : Formation\n\"\"\"\nSalut, on pourrait caler la formation la semaine prochaine ? Ça coûterait combien pour 10 personnes ?\n\"\"\"\n\nMa consigne pour cette réponse (prioritaire) : accepter, proposer mardi après-midi\n\nÉcris le corps de la réponse que j'envoie à Léa.";
                answer = "Merci pour ton message. La semaine prochaine me convient : je te propose mardi après-midi, à partir de 14 h si ça te va.\n\nPour 10 personnes, le coût serait de [à compléter]. Je t'envoie le détail dès qu'on a fixé la date.";
            }
            else
            {
                mail = "Message reçu de Léa Bernard, objet : Formation\n\"\"\"\nBonjour, pourrions-nous organiser la formation la semaine prochaine ? Quel en serait le coût pour 10 personnes ?\n\"\"\"\n\nMa consigne pour cette réponse (prioritaire) : accepter, proposer mardi après-midi\n\nÉcris le corps de la réponse que j'envoie à Léa.";
                answer = "Merci pour votre message. La semaine prochaine me convient : je vous propose mardi après-midi, à partir de 14 h si cela vous convient.\n\nPour 10 personnes, le coût serait de [à compléter]. Je vous enverrai le détail dès que la date sera fixée.";
            }
            yield return new ChatMessage("user", mail);
            yield return new ChatMessage("assistant", answer);
        }

        static List<ChatMessage> Conversation(ReplyFrame frame, string tone, string user)
        {
            var list = new List<ChatMessage> { new ChatMessage("system", SystemForReply(frame, tone)) };
            list.AddRange(Example(frame));
            list.Add(new ChatMessage("user", user));
            return list;
        }

        public static ReplyRequest ReplyToMail(MailItem mail, string instruction, string tone, Settings s)
        {
            var frame = Frame(mail.Sender, mail.Body + " " + mail.Subject, tone, s);
            string who = FirstName(mail.Sender) ?? (string.IsNullOrEmpty(mail.Sender) ? "l'expéditeur" : mail.Sender);
            var user = new StringBuilder();
            user.AppendLine("Message reçu de " + (string.IsNullOrEmpty(mail.Sender) ? "l'expéditeur" : mail.Sender) + ", objet : " + mail.Subject);
            user.AppendLine("\"\"\"");
            user.AppendLine(string.IsNullOrWhiteSpace(mail.Body) ? "(message vide)" : mail.Body);
            user.AppendLine("\"\"\"");
            user.AppendLine();
            AppendInstruction(user, instruction);
            user.Append("Écris le corps de la réponse que j'envoie à " + who + ".");
            return new ReplyRequest { Frame = frame, Messages = Conversation(frame, tone, user.ToString()) };
        }

        public static ReplyRequest ReplyToExcerpt(string excerpt, MailItem context, string instruction, string tone, Settings s)
        {
            string sender = context != null ? context.Sender : null;
            var frame = Frame(sender, excerpt, tone, s);
            var user = new StringBuilder();
            user.AppendLine("Passage sélectionné à l'écran (texte reconnu automatiquement, il peut contenir quelques erreurs de lecture)" +
                            (context != null ? ", extrait du message de " + (sender ?? "l'expéditeur") + ", objet : " + context.Subject : "") + " :");
            user.AppendLine("\"\"\"");
            user.AppendLine(Trim(excerpt, 3500));
            user.AppendLine("\"\"\"");
            user.AppendLine();
            AppendInstruction(user, instruction);
            user.Append("Écris le corps de ma réponse à ce passage précis.");
            return new ReplyRequest { Frame = frame, Messages = Conversation(frame, tone, user.ToString()) };
        }

        public static List<ChatMessage> FreeQuestion(string question, string excerpt)
        {
            const string system = "Tu es Nomi, un assistant qui fonctionne entièrement sur l'ordinateur de l'utilisateur, sans connexion. " +
                                  "Réponds en français sauf si l'on te parle dans une autre langue. Sois clair, exact et concis. " +
                                  "Pas de Markdown décoratif. Si tu ne sais pas, dis-le.";
            string user = question.Trim();
            if (!string.IsNullOrWhiteSpace(excerpt))
                user = "Texte de référence :\n\"\"\"\n" + Trim(excerpt, 3500) + "\n\"\"\"\n\n" + user;
            return new List<ChatMessage> { new ChatMessage("system", system), new ChatMessage("user", user) };
        }

        static void AppendInstruction(StringBuilder sb, string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction)) return;
            sb.AppendLine("Ma consigne pour cette réponse (prioritaire) : " + instruction.Trim());
            sb.AppendLine();
        }

        // ------------------------------------------------------------------ cleaning

        static readonly Regex GreetingLine = new Regex(@"^\s*(Bonjour|Bonsoir|Salut|Hello|Hi|Dear|Cher|Chère|Madame|Monsieur)\b[^\n]{0,60}[,!.]?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex InlineGreeting = new Regex(@"^\s*(Bonjour|Bonsoir|Salut|Hello|Hi|Dear)(\s+[^\s,.!]{1,25}){0,2}\s*[,!]\s+", RegexOptions.IgnoreCase);
        static readonly Regex ClosingLine = new Regex(@"^\s*(Cordialement|Bien cordialement|Bien à (vous|toi)|Belle journée|Bonne journée|À bientôt|A bientôt|Merci( d'avance| beaucoup)?|Sincèrement|Salutations|Je vous prie d'agréer|Best( regards)?|Kind regards|Regards|All the best|Cheers|Thanks|Thank you)\b[^\n]{0,80}$", RegexOptions.IgnoreCase);

        // Keeps the body only: removes what the model adds around it despite the instructions.
        public static string CleanBody(string text)
        {
            var lines = CleanAnswer(text).Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && (lines[0].Trim().Length == 0 || GreetingLine.IsMatch(lines[0]))) lines.RemoveAt(0);
            // "Bonjour Julie, désolé…" on the same line as the text.
            if (lines.Count > 0) lines[0] = InlineGreeting.Replace(lines[0], m => "").TrimStart();
            if (lines.Count > 0 && lines[0].Length > 0) lines[0] = char.ToUpperInvariant(lines[0][0]) + lines[0].Substring(1);
            // From the first closing formula near the end, everything is closing or signature.
            for (int i = Math.Max(0, lines.Count - 5); i < lines.Count; i++)
            {
                if (i > 0 && ClosingLine.IsMatch(lines[i])) { lines.RemoveRange(i, lines.Count - i); break; }
            }
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\r\n", lines).Trim();
        }

        // Removes what small models tend to add around an answer.
        public static string CleanAnswer(string text)
        {
            if (text == null) return "";
            string t = text.Replace("\r\n", "\n").Trim();
            t = Regex.Replace(t, "^```[a-zA-Z]*\\n|\\n?```$", "");
            t = Regex.Replace(t, "^(Objet|Subject)\\s*:.*\\n+", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "^(Voici (une|la|ma) (proposition de )?réponse[^\\n]*:)\\s*\\n+", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "\\*\\*(.+?)\\*\\*", "$1");
            if (t.Length > 1 && t[0] == '"' && t[t.Length - 1] == '"') t = t.Substring(1, t.Length - 2);
            t = Regex.Replace(t, "\\n+\\s*(Nomi|\\[?Votre (nom|prénom)\\]?|\\[?Ton prénom\\]?|\\[?Your name\\]?)\\s*$", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "\\n{3,}", "\n\n");
            return t.Trim().Replace("\n", "\r\n");
        }

        static readonly Regex[] HistoryMarkers =
        {
            new Regex(@"^-{2,}\s*(Original Message|Message d'origine|Message d’origine|Courriel original|Mensaje original)\s*-{2,}", RegexOptions.IgnoreCase | RegexOptions.Multiline),
            new Regex(@"^_{10,}\s*$", RegexOptions.Multiline),
            new Regex(@"^\s*(De|From)\s*:.*\n\s*(Envoyé|Sent|Date)\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline),
            new Regex(@"^\s*(Le|On)\s.{6,120}(a écrit|wrote)\s*:\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)
        };

        // Keeps the new part of a message: drops the quoted history and long runs of blank
        // lines, then caps the length for the model's context.
        public static string CleanMailBody(string body, int max)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string t = body.Replace("\r\n", "\n").Replace(' ', ' ');
            int cut = t.Length;
            foreach (var marker in HistoryMarkers)
            {
                var m = marker.Match(t);
                if (m.Success && m.Index > 20 && m.Index < cut) cut = m.Index;
            }
            t = t.Substring(0, cut);
            t = Regex.Replace(t, "<https?://[^>]+>", "");
            t = Regex.Replace(t, "[ \t]+\n", "\n");
            t = Regex.Replace(t, "\n{3,}", "\n\n");
            return Trim(t.Trim(), max);
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + " […]";
        }
    }
}
