using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nomi
{
    enum EngineState { Off, Starting, Ready, Failed }

    class ChatMessage
    {
        public string Role;
        public string Content;
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    // Runs the bundled llama.cpp server (engine\llama-server.exe) on a private loopback port and
    // talks to it through its OpenAI-compatible API. Nothing leaves the machine.
    //  - one generation at a time; an interactive request cancels any background one
    //  - the server is tied to Nomi through a job object, restarted on demand after a crash,
    //    and unloaded after a configurable idle time to give the memory back
    class LlmEngine : IDisposable
    {
        public static readonly string AppDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        static readonly string EngineDir = Path.Combine(AppDir, "engine");
        static readonly string ServerExe = Path.Combine(EngineDir, "llama-server.exe");
        static readonly string EngineLog = Path.Combine(Log.Folder, "engine.log");

        readonly object gate = new object();
        readonly SemaphoreSlim generation = new SemaphoreSlim(1, 1);
        readonly Queue<string> tail = new Queue<string>();
        readonly Timer idleTimer;
        Settings settings;
        Process server;
        int port;
        // Random per start: only Nomi can use the server (it listens on loopback, but any local
        // program or web page could otherwise reach it).
        string apiKey;
        string runningSignature;
        Task<bool> startTask;
        CancellationTokenSource backgroundRun;
        DateTime lastUse = DateTime.UtcNow;
        readonly List<DateTime> crashes = new List<DateTime>();
        StreamWriter logWriter;
        bool disposed;

        public EngineState State { get; private set; }
        public string StatusText { get; private set; }
        public bool Busy { get { return generation.CurrentCount == 0; } }

        // Raised on a worker thread.
        public event Action StateChanged;

        public LlmEngine(Settings settings)
        {
            this.settings = settings.Clone();
            StatusText = "Modèle non chargé";
            idleTimer = new Timer(delegate { UnloadIfIdle(); }, null, 60000, 60000);
        }

        public void UpdateSettings(Settings s)
        {
            string before = Signature(settings);
            settings = s.Clone();
            if (before != Signature(settings) && State != EngineState.Off)
            {
                Log.Write("Model settings changed, restarting the engine");
                Stop();
                if (settings.PreloadModel) Preload();
            }
        }

        static string Signature(Settings s)
        {
            return ResolveModel(s) + "|" + s.Threads + "|" + s.ContextSize;
        }

        public static string ResolveModel(Settings s)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(s.ModelPath) && File.Exists(s.ModelPath)) return s.ModelPath;
                foreach (string dir in new[] { Path.Combine(AppDir, "models"), Path.Combine(Log.Folder, "models") })
                {
                    if (!Directory.Exists(dir)) continue;
                    string found = Directory.GetFiles(dir, "*.gguf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (found != null) return found;
                }
            }
            catch (Exception e) { Log.Error("ResolveModel", e); }
            return null;
        }

        public void Preload()
        {
            var ignored = EnsureStartedAsync();
        }

        Task<bool> EnsureStartedAsync()
        {
            lock (gate)
            {
                if (disposed) return Task.FromResult(false);
                if (State == EngineState.Ready && server != null && !HasExited(server)) return Task.FromResult(true);
                if (startTask != null && !startTask.IsCompleted) return startTask;
                startTask = Task.Run(() => StartServer());
                return startTask;
            }
        }

        bool StartServer()
        {
            string model = ResolveModel(settings);
            if (!File.Exists(ServerExe)) return Fail("Moteur introuvable (" + ServerExe + ")");
            if (model == null) return Fail("Aucun modèle .gguf trouvé");

            lock (crashes)
            {
                crashes.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(3));
                if (crashes.Count >= 3) return Fail("Le moteur s'est arrêté plusieurs fois. Voir engine.log.");
            }

            KillServer();
            SetState(EngineState.Starting, "Chargement du modèle…");
            port = FreePort();
            apiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            int threads = settings.Threads > 0 ? (int)settings.Threads : Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));

            var psi = new ProcessStartInfo(ServerExe)
            {
                Arguments = "-m \"" + model + "\" --host 127.0.0.1 --port " + port + " -c " + (int)settings.ContextSize +
                            " -t " + threads + " -np 1 --no-webui -a nomi --api-key " + apiKey,
                WorkingDirectory = EngineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // Ignore any LLAMA_ARG_* the user may have in their environment.
            foreach (string key in psi.EnvironmentVariables.Keys.Cast<string>().ToList())
                if (key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)) psi.EnvironmentVariables.Remove(key);

            try
            {
                OpenEngineLog();
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => EngineOutput(e.Data);
                p.ErrorDataReceived += (s, e) => EngineOutput(e.Data);
                p.Exited += delegate { OnServerExited(p); };
                p.Start();
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                Native.TieToThisProcess(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                lock (gate) server = p;
                runningSignature = Signature(settings);
                Log.Write("Engine started (pid " + p.Id + ", port " + port + ", " + threads + " threads): " + Path.GetFileName(model));
            }
            catch (Exception e)
            {
                Log.Error("Engine start", e);
                return Fail("Impossible de lancer le moteur : " + e.Message);
            }

            // Wait for the model to be loaded (HTTP 200 on /health).
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(4))
            {
                Process p;
                lock (gate) p = server;
                if (p == null || HasExited(p)) return Fail("Le moteur s'est arrêté au chargement. " + LastEngineLines());
                if (Healthy()) { lastUse = DateTime.UtcNow; SetState(EngineState.Ready, "Prêt · " + Path.GetFileNameWithoutExtension(model)); return true; }
                Thread.Sleep(350);
            }
            KillServer();
            return Fail("Le modèle n'a pas fini de charger à temps");
        }

        bool Healthy()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                req.Proxy = null;
                req.Timeout = 2000;
                req.KeepAlive = false;
                using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        void OnServerExited(Process p)
        {
            bool expected;
            lock (gate)
            {
                expected = server != p;
                if (!expected) server = null;
            }
            if (expected || disposed) return;
            int code = -1;
            try { code = p.ExitCode; } catch { }
            Log.Write("Engine exited unexpectedly (code " + code + "). " + LastEngineLines());
            lock (crashes) crashes.Add(DateTime.UtcNow);
            SetState(EngineState.Off, "Moteur arrêté, il redémarrera à la prochaine demande");
        }

        // Streams a chat completion. onToken is called on a worker thread with each new chunk.
        // Background requests give way to interactive ones (they are cancelled, the caller retries later).
        public async Task<string> ChatAsync(IList<ChatMessage> messages, int maxTokens, double temperature,
                                            Action<string> onToken, CancellationToken cancel, bool background)
        {
            CancellationTokenSource linked = null;
            if (background)
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cancel = linked.Token;
            }
            else
            {
                CancellationTokenSource running;
                lock (gate) running = backgroundRun;
                if (running != null) try { running.Cancel(); } catch (ObjectDisposedException) { }
            }

            try
            {
                await generation.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    if (background) lock (gate) backgroundRun = linked;
                    lastUse = DateTime.UtcNow;
                    if (!await EnsureStartedAsync().ConfigureAwait(false))
                        throw new InvalidOperationException(StatusText);
                    cancel.ThrowIfCancellationRequested();
                    for (int attempt = 0; ; attempt++)
                    {
                        WebException failure;
                        try
                        {
                            return await Task.Run(() => Stream(messages, maxTokens, temperature, onToken, cancel)).ConfigureAwait(false);
                        }
                        catch (WebException e) { failure = e; }

                        if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                        bool lost = failure.Status == WebExceptionStatus.ConnectFailure || failure.Status == WebExceptionStatus.ConnectionClosed;
                        if (!lost || attempt > 0) throw new InvalidOperationException(DescribeWebError(failure));
                        // The server may have died between the health check and the request: restart it once.
                        Log.Write("Engine connection lost, restarting: " + failure.Message);
                        KillServer();
                        SetState(EngineState.Off, "Redémarrage du moteur…");
                        if (!await EnsureStartedAsync().ConfigureAwait(false)) throw new InvalidOperationException(StatusText);
                    }
                }
                finally
                {
                    if (background) lock (gate) if (backgroundRun == linked) backgroundRun = null;
                    lastUse = DateTime.UtcNow;
                    generation.Release();
                }
            }
            finally
            {
                if (linked != null) linked.Dispose();
            }
        }

        string Stream(IList<ChatMessage> messages, int maxTokens, double temperature, Action<string> onToken, CancellationToken cancel)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var body = new Dictionary<string, object>
            {
                { "model", "nomi" },
                { "messages", messages.Select(m => new Dictionary<string, object> { { "role", m.Role }, { "content", m.Content } }).ToArray() },
                { "stream", true },
                { "max_tokens", maxTokens },
                { "temperature", temperature },
                { "top_p", 0.9 },
                { "repeat_penalty", 1.07 },
                { "cache_prompt", true }
            };
            byte[] payload = Encoding.UTF8.GetBytes(json.Serialize(body));

            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/v1/chat/completions");
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Proxy = null;
            req.KeepAlive = false;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 180000;
            req.ContentLength = payload.Length;
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;

            var text = new StringBuilder();
            using (cancel.Register(() => { try { req.Abort(); } catch { } }))
            {
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (!line.StartsWith("data:")) continue;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") break;
                        string chunk = ExtractDelta(json, data);
                        if (string.IsNullOrEmpty(chunk)) continue;
                        text.Append(chunk);
                        lastUse = DateTime.UtcNow;
                        if (onToken != null) onToken(chunk);
                    }
                }
            }
            return text.ToString();
        }

        static string ExtractDelta(JavaScriptSerializer json, string data)
        {
            try
            {
                var root = json.DeserializeObject(data) as Dictionary<string, object>;
                if (root == null) return null;
                object error;
                if (root.TryGetValue("error", out error)) throw new InvalidOperationException("Erreur du moteur : " + Describe(error));
                object choices;
                if (!root.TryGetValue("choices", out choices)) return null;
                var list = choices as IList;
                if (list == null || list.Count == 0) return null;
                var first = list[0] as Dictionary<string, object>;
                object delta;
                if (first == null || !first.TryGetValue("delta", out delta)) return null;
                var d = delta as Dictionary<string, object>;
                object content;
                return d != null && d.TryGetValue("content", out content) ? content as string : null;
            }
            catch (ArgumentException) { return null; }   // malformed chunk: skip it
        }

        static string Describe(object error)
        {
            var d = error as Dictionary<string, object>;
            object message;
            return d != null && d.TryGetValue("message", out message) ? Convert.ToString(message) : Convert.ToString(error);
        }

        static string DescribeWebError(WebException e)
        {
            try
            {
                var resp = e.Response as HttpWebResponse;
                if (resp != null)
                    using (var r = new StreamReader(resp.GetResponseStream()))
                    {
                        string body = r.ReadToEnd();
                        if (body.Length > 300) body = body.Substring(0, 300);
                        return "Erreur du moteur (" + (int)resp.StatusCode + ") : " + body;
                    }
            }
            catch { }
            return "Erreur du moteur : " + e.Message;
        }

        void UnloadIfIdle()
        {
            try
            {
                if (settings.UnloadAfterMinutes <= 0 || State != EngineState.Ready || Busy) return;
                if (DateTime.UtcNow - lastUse < TimeSpan.FromMinutes(settings.UnloadAfterMinutes)) return;
                Log.Write("Engine idle, unloading the model");
                Stop();
                SetState(EngineState.Off, "En veille · rechargé à la prochaine demande");
            }
            catch (Exception e) { Log.Error("UnloadIfIdle", e); }
        }

        public void Stop()
        {
            KillServer();
            if (State != EngineState.Failed) SetState(EngineState.Off, "Modèle non chargé");
        }

        void KillServer()
        {
            Process p;
            lock (gate) { p = server; server = null; }
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
            try { p.Dispose(); } catch { }
        }

        bool Fail(string message)
        {
            Log.Write("Engine: " + message);
            SetState(EngineState.Failed, message);
            return false;
        }

        void SetState(EngineState state, string text)
        {
            State = state;
            StatusText = text;
            var handler = StateChanged;
            if (handler != null) try { handler(); } catch (Exception e) { Log.Error("Engine StateChanged", e); }
        }

        // A failed engine may be retried (for example after the user picked another model).
        public void ResetFailure()
        {
            lock (crashes) crashes.Clear();
            if (State == EngineState.Failed) SetState(EngineState.Off, "Modèle non chargé");
        }

        void OpenEngineLog()
        {
            lock (tail)
            {
                try
                {
                    if (logWriter != null) logWriter.Dispose();
                    Directory.CreateDirectory(Log.Folder);
                    logWriter = new StreamWriter(EngineLog, false, new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch { logWriter = null; }
                tail.Clear();
            }
        }

        void EngineOutput(string line)
        {
            if (line == null) return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 40) tail.Dequeue();
                try { if (logWriter != null && logWriter.BaseStream.Length < 4 * 1024 * 1024) logWriter.WriteLine(line); } catch { }
            }
        }

        string LastEngineLines()
        {
            lock (tail)
            {
                var errors = tail.Where(l => l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                return errors.Count > 0 ? errors.Last() : "";
            }
        }

        static bool HasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int p = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return p;
        }

        public void Dispose()
        {
            disposed = true;
            idleTimer.Dispose();
            KillServer();
            lock (tail) if (logWriter != null) { logWriter.Dispose(); logWriter = null; }
        }
    }
}
