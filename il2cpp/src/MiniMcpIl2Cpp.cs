// MiniMCP (IL2CPP Edition) v0.1.0
// BepInEx 6 IL2CPP plugin that turns a Unity IL2CPP game into a runtime HTTP bridge.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiniMCP.Il2Cpp
{
    [BepInPlugin(Guid, "MiniMCP (IL2CPP)", Version)]
    public class MiniMcpPlugin : BasePlugin
    {
        public const string Guid = "com.operit.minimcp.il2cpp";
        public const string Version = "0.3.0";
        public const int DefaultPort = 18081;

        internal static ManualLogSource Logger;
        internal static int ListenPort = DefaultPort;

        public override void Load()
        {
            Logger = Log;
            Log.LogInfo("[MiniMCP] loading (IL2CPP edition)...");
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<MiniMcpBehaviour>();
                Log.LogInfo("[MiniMCP] behaviour registered");
            }
            catch (Exception e)
            {
                Log.LogWarning("[MiniMCP] ClassInjector: " + e.Message);
            }
            var comp = AddComponent<MiniMcpBehaviour>();
            Log.LogInfo("[MiniMCP] component attached: " + (comp != null));
        }
    }

    public class MiniMcpBehaviour : MonoBehaviour
    {
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();
        private readonly object _logLock = new object();
        private readonly List<string> _logRing = new List<string>();
        private int _frameCount;
        private string _bootInfo = "";

        public MiniMcpBehaviour(IntPtr ptr) : base(ptr) { }

        private void AddLog(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
            lock (_logLock)
            {
                _logRing.Add(line);
                if (_logRing.Count > 300) _logRing.RemoveAt(0);
            }
            try { if (MiniMcpPlugin.Logger != null) MiniMcpPlugin.Logger.LogInfo("[MiniMCP] " + msg); } catch { }
        }

        private void Awake()
        {
            try
            {
                AddLog("Awake on frame " + Time.frameCount);
                _bootInfo = "unity=" + Application.unityVersion + " product=" + Application.productName + " ver=" + Application.version;
                AddLog(_bootInfo);
                StartServer();
            }
            catch (Exception e)
            {
                AddLog("Awake failed: " + e);
            }
        }

        private void StartServer()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, MiniMcpPlugin.ListenPort);
                _listener.Start();
                _running = true;
                _thread = new Thread(AcceptLoop);
                _thread.IsBackground = true;
                _thread.Start();
                AddLog("HTTP listening on http://127.0.0.1:" + MiniMcpPlugin.ListenPort + "/");
            }
            catch (Exception e)
            {
                AddLog("server start failed: " + e.Message);
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(new WaitCallback(HandleClient), client);
                }
                catch (Exception)
                {
                    if (_running) Thread.Sleep(30);
                }
            }
        }

        private T RunOnMain<T>(Func<T> fn, T fallback)
        {
            T result = fallback;
            bool failed = false;
            var done = new ManualResetEventSlim(false);
            _mainQueue.Enqueue(() =>
            {
                try { result = fn(); }
                catch (Exception e) { failed = true; AddLog("main task error: " + e.Message); }
                finally { done.Set(); }
            });
            if (!done.Wait(5000)) AddLog("main task timeout");
            return failed ? fallback : result;
        }

        private void Update()
        {
            _frameCount = Time.frameCount;
            Action act;
            int guard = 0;
            while (_mainQueue.TryDequeue(out act) && guard++ < 64)
            {
                try { act(); } catch (Exception e) { AddLog("queued error: " + e.Message); }
            }
        }

        private void OnDestroy()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            AddLog("OnDestroy (server stopped)");
        }

        // ------------------------------------------------ HTTP layer
        private void HandleClient(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                NetworkStream ns = client.GetStream();
                string head = ReadHead(ns);
                string bodyStr = "";
                if (string.IsNullOrEmpty(head)) return;

                string[] lines = head.Split('\n');
                string first = lines[0].Trim();
                string[] parts = first.Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0].ToUpperInvariant();
                string rawPath = parts[1];

                int contentLength = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    int c = ln.IndexOf(':');
                    if (c <= 0) continue;
                    string k = ln.Substring(0, c).Trim().ToLowerInvariant();
                    string v = ln.Substring(c + 1).Trim();
                    if (k == "content-length")
                    {
                        int.TryParse(v, out contentLength);
                    }
                }
                if (contentLength > 0)
                {
                    byte[] buf = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = ns.Read(buf, read, contentLength - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    bodyStr = Encoding.UTF8.GetString(buf, 0, read);
                }

                string resp;
                try { resp = Route(method, rawPath, bodyStr); }
                catch (Exception e) { resp = ErrJson(e.Message); }

                byte[] outBytes = Encoding.UTF8.GetBytes(resp);
                string headerStr = "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json; charset=utf-8\r\n"
                    + "Content-Length: " + outBytes.Length + "\r\n"
                    + "Access-Control-Allow-Origin: *\r\n"
                    + "Connection: close\r\n\r\n";
                byte[] headBytes = Encoding.UTF8.GetBytes(headerStr);
                ns.Write(headBytes, 0, headBytes.Length);
                ns.Write(outBytes, 0, outBytes.Length);
                ns.Flush();
            }
            catch (Exception e)
            {
                AddLog("client error: " + e.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private string ReadHead(NetworkStream ns)
        {
            var sb = new StringBuilder();
            byte[] one = new byte[1];
            int total = 0;
            while (total < 32768)
            {
                int n = ns.Read(one, 0, 1);
                if (n <= 0) break;
                total++;
                sb.Append((char)one[0]);
                string cur = sb.ToString();
                if (cur.EndsWith("\r\n\r\n") || cur.EndsWith("\n\n")) break;
            }
            return sb.ToString();
        }

        private static string Num(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private const char Qc = (char)34;

        // ===== 作弊状态（逐帧） =====
        private static Transform _playerTf = null;
        private static bool _noclip = false;
        private static string _playerName = "";
        private static string _cheatMsg = "";

        private static Transform FindPlayer()
        {
            string[] cand = new string[] { "Player", "GameController/Player", "Person", "HeadPlayer", "Player(Clone)" };
            foreach (string n in cand)
            {
                try
                {
                    GameObject g = GameObject.Find(n);
                    if (g != null) { _playerName = n; return g.transform; }
                    Transform tp = GameObject.Find("GameController").transform.Find(n);
                    if (tp != null) { _playerName = n; return tp; }
                }
                catch { }
            }
            return null;
        }

        private static string PlayerJson()
        {
            Transform t = _playerTf != null ? _playerTf : FindPlayer();
            _playerTf = t;
            if (t == null) return ErrJson("player not found (try /objects?name=Player)");
            Vector3 p = t.position;
            string comps = "";
            try
            {
                GameObject g = t.gameObject;
                var comp = g.GetComponent<CharacterController>();
                comps = "," + Qc + "characterController" + Qc + ":" + (comp != null ? "true" : "false");
                if (comp != null) comps += Qc + "detectCollisions" + Qc + ":" + (comp.detectCollisions ? "true" : "false");
                var rb = g.GetComponent<Rigidbody>();
                comps += "," + Qc + "rigidbody" + Qc + ":" + (rb != null ? "true" : "false");
                if (rb != null) comps += "," + Qc + "isKinematic" + Qc + ":" + (rb.isKinematic ? "true" : "false") + "," + Qc + "gravity" + Qc + ":" + Num(rb.useGravity ? 1 : 0) + "," + Qc + "velY" + Qc + ":" + Num(rb.velocity.y);
                comps += "," + Qc + "parent" + Qc + ":" + Qc + JsonEsc(t.parent != null ? t.parent.name : "-") + Qc;
                int nch = t.childCount;
                comps += "," + Qc + "children" + Qc + ":" + nch;
                string kid = "";
                for (int i = 0; i < nch && i < 12; i++) { Transform ct = t.GetChild(i); if (i > 0) kid += ","; kid += JsonEsc(ct.name); }
                comps += "," + Qc + "childNames" + Qc + ":[" + Qc + kid.Replace(",", Qc + "," + Qc) + Qc + "]";
            }
            catch (Exception e) { comps = Qc + "ccErr" + Qc + ":" + Qc + JsonEsc(e.Message) + Qc; }
            return "{" + Qc + "ok" + Qc + ":true," + Qc + "player" + Qc + ":" + Qc + JsonEsc(_playerName) + Qc +
                   "," + Qc + "x" + Qc + ":" + Num(p.x) + "," + Qc + "y" + Qc + ":" + Num(p.y) + "," + Qc + "z" + Qc + ":" + Num(p.z) +
                   "," + Qc + "noclip" + Qc + ":" + (_noclip ? "true" : "false") + comps + "}";
        }

        private static string DoGround(string body)
        {
            Transform t = _playerTf != null ? _playerTf : FindPlayer();
            _playerTf = t;
            if (t == null) return ErrJson("player not found");
            try
            {
                var rb4 = t.gameObject.GetComponent<Rigidbody>();
                if (rb4 != null) { rb4.isKinematic = false; rb4.useGravity = true; }
                Vector3 p = t.position;
                p.y = 0.0001f;
                t.position = p;
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "y" + Qc + ":" + Num(0.0001f) + "}";
            }
            catch (Exception e) { return ErrJson("ground failed: " + e.Message); }
        }

        private static string DoNoclip(string body)
        {
            float onf = GetJsonNum(body, "on", 1f);
            SetNoclip(onf > 0.5f);
            return "{" + Qc + "ok" + Qc + ":true," + Qc + "noclip" + Qc + ":" + (_noclip ? "true" : "false") +
                   "," + Qc + "msg" + Qc + ":" + Qc + JsonEsc(_cheatMsg) + Qc +
                   "," + Qc + "player" + Qc + ":" + Qc + JsonEsc(_playerName) + Qc + "}";
        }

        private static void SetNoclip(bool on)
        {
            Transform t = _playerTf != null ? _playerTf : FindPlayer();
            _playerTf = t;
            if (t == null) { _cheatMsg = "player not found"; return; }
            try
            {
                var rb2 = t.gameObject.GetComponent<Rigidbody>();
                if (rb2 != null) { rb2.detectCollisions = !on; _noclip = on; _cheatMsg = "rb.detectCollisions=" + (!on) + " (rigidbody)"; }
                else
                {
                    var cc = t.gameObject.GetComponent<CharacterController>();
                    if (cc != null) { cc.detectCollisions = !on; _noclip = on; _cheatMsg = "cc.detectCollisions=" + (!on); }
                    else { _cheatMsg = "no Rigidbody/CharacterController on " + _playerName; }
                }
            }
            catch (Exception e) { _cheatMsg = "err: " + e.Message; }
        }

        private static string ErrJson(string msg)
        {
            return "{" + Qc + "ok" + Qc + ":false," + Qc + "error" + Qc + ":" + Qc + JsonEsc(msg) + Qc + "}";
        }

        private static string JsonEsc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c == Qc) sb.Append('\\').Append(Qc);
                else if (c == '\\') sb.Append('\\').Append('\\');
                else if (c == '\n') sb.Append('\\').Append('n');
                else if (c == '\r') sb.Append('\\').Append('r');
                else if (c == '\t') sb.Append('\\').Append('t');
                else if (c < (char)0x20) sb.Append('\\').Append('u').Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }

            return sb.ToString();
        }

        // ------------------------------------------------ router
        private string Route(string method, string rawPath, string body)
        {
            string path = rawPath;
            string query = "";
            int q = rawPath.IndexOf('?');
            if (q >= 0)
            {
                path = rawPath.Substring(0, q);
                query = rawPath.Substring(q + 1);
            }
            path = path.TrimEnd('/');
            if (path.Length == 0) path = "/";

            if (path == "/")
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "name" + Qc + ":" + Qc + "MiniMCP (IL2CPP)" + Qc
                     + "," + Qc + "endpoints" + Qc + ":[" + Qc + "/health" + Qc + "," + Qc + "/ping" + Qc
                     + "," + Qc + "/log" + Qc + "," + Qc + "/scene" + Qc + "," + Qc + "/objects?name=xxx" + Qc + "]}";

            if (path == "/health")
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "edition" + Qc + ":" + Qc + "il2cpp" + Qc
                     + "," + Qc + "version" + Qc + ":" + Qc + MiniMcpPlugin.Version + Qc
                     + "," + Qc + "port" + Qc + ":" + MiniMcpPlugin.ListenPort
                     + "," + Qc + "unity" + Qc + ":" + Qc + JsonEsc(Application.unityVersion) + Qc
                     + "," + Qc + "product" + Qc + ":" + Qc + JsonEsc(Application.productName) + Qc + "}";

            if (path == "/ping")
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "pong" + Qc + ":true," + Qc + "frame" + Qc + ":"
                     + RunOnMain<int>(delegate () { return Time.frameCount; }, -1) + "}";

            if (path == "/log")
            {
                string[] lines;
                lock (_logLock) { lines = _logRing.ToArray(); }
                var sb = new StringBuilder();
                sb.Append("{").Append(Qc).Append("ok").Append(Qc).Append(":true,").Append(Qc).Append("count").Append(Qc).Append(":").Append(lines.Length);
                sb.Append(",").Append(Qc).Append("lines").Append(Qc).Append(":[");
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Qc).Append(JsonEsc(lines[i])).Append(Qc);
                }
                sb.Append("]}");
                return sb.ToString();
            }

            if (path == "/scene")
            {
                return RunOnMain<string>(delegate ()
                {
                    var sc = SceneManager.GetActiveScene();
                    int roots = sc.IsValid() ? sc.GetRootGameObjects().Length : -1;
                    int bi = sc.buildIndex;
                    string sn = sc.name;
                    string sp = sc.path;
                    int total = SceneManager.sceneCount;
                    return "{" + Qc + "ok" + Qc + ":true," + Qc + "scene" + Qc + ":" + Qc + JsonEsc(sn) + Qc
                         + "," + Qc + "path" + Qc + ":" + Qc + JsonEsc(sp) + Qc
                         + "," + Qc + "buildIndex" + Qc + ":" + bi
                         + "," + Qc + "rootObjects" + Qc + ":" + roots
                         + "," + Qc + "allScenes" + Qc + ":" + total + "}";
                }, ErrJson("scene query failed"));
            }

            if (path == "/objects")
            {
                string nameFilter = "";
                string[] kvs = query.Split('&');
                for (int i = 0; i < kvs.Length; i++)
                {
                    int eq = kvs[i].IndexOf('=');
                    if (eq > 0 && kvs[i].Substring(0, eq) == "name")
                        nameFilter = Uri.UnescapeDataString(kvs[i].Substring(eq + 1));
                }
                string f = nameFilter;
                return RunOnMain<string>(delegate () { return DumpObjects(f); }, ErrJson("query failed"));
            }

            
            if (method == "POST" && path == "/setactive") return DoSetActive(body);
            if (method == "POST" && path == "/move") return DoMove(body);
            if (method == "POST" && path == "/timescale") return DoTimeScale(body);
            if (method == "POST" && path == "/listfields") return DoListFields(body);
            if (path == "/fields") return RunOnMain<string>(delegate () { GameObject g = FindGO(QueryOf(rawPath, "name")); return g == null ? ErrJson("not found") : DumpFieldsSafe(g); }, ErrJson("fields failed"));
            if (path == "/tree") return RunOnMain<string>(delegate () { GameObject g = FindGO(QueryOf(rawPath, "name")); if (g == null) return ErrJson("not found"); return DumpTree(g, (int)QueryNum(rawPath, "depth", 3)); }, ErrJson("tree failed"));
            if (path == "/stats") return DoStats();
            if (method == "POST" && path == "/get") return DoGetField(body);
            if (method == "POST" && path == "/set") return DoSetField(body);
            if (method == "POST" && path == "/call") return DoCallMethod(body);
            if (path == "/player") return RunOnMain<string>(delegate () { return PlayerJson(); }, ErrJson("player query failed"));
            if (method == "POST" && path == "/noclip") return RunOnMain<string>(delegate () { return DoNoclip(body); }, ErrJson("noclip failed"));
            if (method == "POST" && path == "/ground") return RunOnMain<string>(delegate () { return DoGround(body); }, ErrJson("ground failed"));
            return ErrJson("unknown endpoint");
        }

        private string DumpObjects(string nameFilter)
        {
            GameObject[] all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var sb = new StringBuilder();
            int shown = 0;
            sb.Append("{").Append(Qc).Append("ok").Append(Qc).Append(":true,").Append(Qc).Append("total").Append(Qc).Append(":").Append(all.Length);
            sb.Append(",").Append(Qc).Append("items").Append(Qc).Append(":[");
            for (int i = 0; i < all.Length && shown < 200; i++)
            {
                GameObject go = all[i];
                if (go == null) continue;
                string nm = go.name;
                if (nm == null) nm = "";
                if (nameFilter.Length > 0 && nm.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (shown > 0) sb.Append(',');
                shown++;
                Vector3 pos = go.transform.position;
                sb.Append("{").Append(Qc).Append("name").Append(Qc).Append(":").Append(Qc).Append(JsonEsc(nm)).Append(Qc);
                sb.Append(",").Append(Qc).Append("active").Append(Qc).Append(":").Append(go.activeInHierarchy ? "true" : "false");
                sb.Append(",").Append(Qc).Append("children").Append(Qc).Append(":").Append(go.transform.childCount);
                sb.Append(",").Append(Qc).Append("px").Append(Qc).Append(":").Append(pos.x);
                sb.Append(",").Append(Qc).Append("py").Append(Qc).Append(":").Append(pos.y);
                sb.Append(",").Append(Qc).Append("pz").Append(Qc).Append(":").Append(pos.z);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ------------------------------------------------ v0.2.0 write APIs
        private static string GetJsonStr(string body, string key)
        {
            if (body == null || body.Length == 0) return null;
            string pat = Qc + key + Qc;
            int i = body.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i += pat.Length;
            while (i < body.Length && (body[i] == ':' || body[i] == ' ')) i++;
            if (i >= body.Length || body[i] != Qc) return null;
            i++;
            int j = body.IndexOf(Qc, i);
            if (j < 0) return null;
            return body.Substring(i, j - i);
        }

        private static float GetJsonNum(string body, string key, float def)
        {
            if (body == null || body.Length == 0) return def;
            int i = body.IndexOf(Qc + key + Qc, StringComparison.Ordinal);
            if (i < 0) return def;
            i += key.Length + 2;
            while (i < body.Length && (body[i] == ':' || body[i] == ' ')) i++;
            int st = i;
            if (st < body.Length && (body[st] == '-' || body[st] == '+')) i++;
            while (i < body.Length)
            {
                char ch = body[i];
                bool ok = (ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == 'e' || ch == 'E' || ch == '+';
                if (!ok) break;
                i++;
            }
            if (i <= st) return def;
            float f;
            if (float.TryParse(body.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            return def;
        }

        private GameObject FindGO(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            GameObject[] all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject g = all[i];
                if (g == null || g.name == null) continue;
                if (g.name == name) return g;
            }
            for (int i = 0; i < all.Length; i++)
            {
                GameObject g = all[i];
                if (g == null || g.name == null) continue;
                if (g.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return g;
            }
            return null;
        }

        private string DoSetActive(string body)
        {
            string nm = GetJsonStr(body, "name");
            string act = GetJsonStr(body, "active");
            if (string.IsNullOrEmpty(nm)) return ErrJson("missing name");
            bool on = !(act == "false" || act == "0" || act == "off");
            return RunOnMain<string>(delegate ()
            {
                GameObject go = FindGO(nm);
                if (go == null) return ErrJson("object not found: " + nm);
                go.SetActive(on);
                string realName = go.name;
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "target" + Qc + ":" + Qc + JsonEsc(realName) + Qc
                     + "," + Qc + "active" + Qc + ":" + (on ? "true" : "false") + "}";
            }, ErrJson("setactive failed"));
        }

        private string DoMove(string body)
        {
            string nm = GetJsonStr(body, "name");
            if (string.IsNullOrEmpty(nm)) return ErrJson("missing name");
            float x = GetJsonNum(body, "x", float.NaN);
            float y = GetJsonNum(body, "y", float.NaN);
            float z = GetJsonNum(body, "z", float.NaN);
            return RunOnMain<string>(delegate ()
            {
                GameObject go = FindGO(nm);
                if (go == null) return ErrJson("object not found: " + nm);
                Vector3 p = go.transform.position;
                if (!float.IsNaN(x)) p.x = x;
                if (!float.IsNaN(y)) p.y = y;
                if (!float.IsNaN(z)) p.z = z;
                go.transform.position = p;
                Vector3 q = go.transform.position;
                string realName = go.name;
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "target" + Qc + ":" + Qc + JsonEsc(realName) + Qc
                     + "," + Qc + "x" + Qc + ":" + q.x + "," + Qc + "y" + Qc + ":" + q.y
                     + "," + Qc + "z" + Qc + ":" + q.z + "}";
            }, ErrJson("move failed"));
        }

        private string DoTimeScale(string body)
        {
            float v = GetJsonNum(body, "value", 1f);
            return RunOnMain<string>(delegate ()
            {
                Time.timeScale = v;
                return "{" + Qc + "ok" + Qc + ":true," + Qc + "timeScale" + Qc + ":" + Time.timeScale + "}";
            }, ErrJson("timescale failed"));
        }

        private string DoListFields(string body)
        {
            string nm = GetJsonStr(body, "name");
            if (string.IsNullOrEmpty(nm)) return ErrJson("missing name");
            return RunOnMain<string>(delegate ()
            {
                GameObject go = FindGO(nm);
                if (go == null) return ErrJson("object not found: " + nm);
                var comps = go.GetComponents<Component>();
                var sb = new StringBuilder();
                sb.Append("{").Append(Qc).Append("ok").Append(Qc).Append(":true,").Append(Qc).Append("target").Append(Qc).Append(":").Append(Qc).Append(JsonEsc(go.name)).Append(Qc);
                sb.Append(",").Append(Qc).Append("componentCount").Append(Qc).Append(":").Append(comps.Length);
                sb.Append(",").Append(Qc).Append("components").Append(Qc).Append(":[");
                for (int i = 0; i < comps.Length && i < 40; i++)
                {
                    if (i > 0) sb.Append(',');
                    string tn = "null";
                    if (comps[i] != null) { tn = comps[i].GetType().Name; }
                    sb.Append(Qc).Append(JsonEsc(tn)).Append(Qc);
                }
                sb.Append("]}");
                return sb.ToString();
            }, ErrJson("listfields failed"));
        }


        // ================================================ v0.3.0 IL2CPP reflection
        private const int T_BOOLEAN = 0x02;
        private const int T_I1 = 0x03;
        private const int T_U1 = 0x04;
        private const int T_I2 = 0x05;
        private const int T_U2 = 0x06;
        private const int T_I4 = 0x08;
        private const int T_U4 = 0x09;
        private const int T_I8 = 0x0A;
        private const int T_U8 = 0x0B;
        private const int T_R4 = 0x0C;
        private const int T_R8 = 0x0D;
        private const int T_STRING = 0x0E;

        private static unsafe string PtrStr(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            IntPtr s = (IntPtr)IL2CPP.il2cpp_string_chars(p);
            if (s == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(s);
        }

        private static string ClassName(IntPtr klass)
        {
            if (klass == IntPtr.Zero) return "?";
            IntPtr np = IL2CPP.il2cpp_class_get_name(klass);
            IntPtr nsp = IL2CPP.il2cpp_class_get_namespace(klass);
            string n = Marshal.PtrToStringAnsi(np) ?? "?";
            string ns = Marshal.PtrToStringAnsi(nsp) ?? "";
            return ns.Length > 0 ? (ns + "." + n) : n;
        }

        private static string TypeName(IntPtr typePtr)
        {
            if (typePtr == IntPtr.Zero) return "?";
            IntPtr np = IL2CPP.il2cpp_type_get_name(typePtr);
            return Marshal.PtrToStringAnsi(np) ?? "?";
        }

        // 读一个字段的值并转为 JSON 片段
        private static string FieldValueJson(IntPtr field, IntPtr objPtr)
        {
            IntPtr ft = IL2CPP.il2cpp_field_get_type(field);
            int t = (int)IL2CPP.il2cpp_type_get_type(ft);
            int off = (int)IL2CPP.il2cpp_field_get_offset(field);
            if (off == 0) return "null";
            IntPtr p = (IntPtr)((long)objPtr + off);
            switch (t)
            {
                case T_BOOLEAN:
                    return (Marshal.ReadByte(p) != 0) ? "true" : "false";
                case T_I1:
                    return ((sbyte)Marshal.ReadByte(p)).ToString();
                case T_U1:
                    return Marshal.ReadByte(p).ToString();
                case T_I2:
                    return Marshal.ReadInt16(p).ToString();
                case T_U2:
                    return ((ushort)Marshal.ReadInt16(p)).ToString();
                case T_I4:
                    return Marshal.ReadInt32(p).ToString();
                case T_U4:
                    return ((uint)Marshal.ReadInt32(p)).ToString();
                case T_I8:
                    return Marshal.ReadInt64(p).ToString();
                case T_U8:
                    return ((ulong)Marshal.ReadInt64(p)).ToString();
                case T_R4:
                    return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p)), 0).ToString(CultureInfo.InvariantCulture);
                case T_R8:
                    return BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(p)), 0).ToString(CultureInfo.InvariantCulture);
                case T_STRING:
                    {
                        IntPtr sp = Marshal.ReadIntPtr(p);
                        string str = PtrStr(sp);
                        if (str == null) return "null";
                        return Qc + JsonEsc(str) + Qc;
                    }
                default:
                    return "null";
            }
        }

        private static void WriteFieldValue(IntPtr field, IntPtr objPtr, string val)
        {
            IntPtr ft = IL2CPP.il2cpp_field_get_type(field);
            int t = (int)IL2CPP.il2cpp_type_get_type(ft);
            int off = (int)IL2CPP.il2cpp_field_get_offset(field);
            if (off == 0) return;
            IntPtr p = (IntPtr)((long)objPtr + off);
            switch (t)
            {
                case T_BOOLEAN:
                    Marshal.WriteByte(p, (byte)((val == "1" || val == "true") ? 1 : 0));
                    break;
                case T_I1:
                case T_U1:
                    Marshal.WriteByte(p, byte.Parse(val));
                    break;
                case T_I2:
                case T_U2:
                    Marshal.WriteInt16(p, short.Parse(val));
                    break;
                case T_I4:
                case T_U4:
                    Marshal.WriteInt32(p, int.Parse(val));
                    break;
                case T_I8:
                case T_U8:
                    Marshal.WriteInt64(p, long.Parse(val));
                    break;
                case T_R4:
                    Marshal.WriteInt32(p, BitConverter.ToInt32(BitConverter.GetBytes(float.Parse(val, CultureInfo.InvariantCulture)), 0));
                    break;
                case T_R8:
                    Marshal.WriteInt64(p, BitConverter.ToInt64(BitConverter.GetBytes(double.Parse(val, CultureInfo.InvariantCulture)), 0));
                    break;
                default:
                    break;
            }
        }

        // 遍历一个对象的全部组件与字段
        private string DumpFields(GameObject go)
        {
            var comps = go.GetComponents<Component>();
            var sb = new StringBuilder();
            sb.Append("{").Append(Qc).Append("ok").Append(Qc).Append(":true,").Append(Qc).Append("target").Append(Qc).Append(":").Append(Qc).Append(JsonEsc(go.name)).Append(Qc);
            sb.Append(",").Append(Qc).Append("components").Append(Qc).Append(":[");
            for (int ci = 0; ci < comps.Length; ci++)
            {
                if (ci > 0) sb.Append(',');
                Component comp = comps[ci];
                if (comp == null) { sb.Append("null"); continue; }
                IntPtr op = comp.Pointer;
                IntPtr klass = IL2CPP.il2cpp_object_get_class(op);
                sb.Append("{").Append(Qc).Append("class").Append(Qc).Append(":").Append(Qc).Append(JsonEsc(ClassName(klass))).Append(Qc);
                sb.Append(",").Append(Qc).Append("fields").Append(Qc).Append(":[");
                IntPtr iter = IntPtr.Zero;
                IntPtr field;
                int n = 0;
                while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero && n < 60)
                {
                    string fn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                    if (fn == null) continue;
                    int off = (int)IL2CPP.il2cpp_field_get_offset(field);
                    if (off == 0) continue;
                    if (n > 0) sb.Append(',');
                    n++;
                    sb.Append(Qc).Append(JsonEsc(fn)).Append(Qc).Append(":");
                    try { sb.Append(FieldValueJson(field, op)); }
                    catch { sb.Append("null"); }
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }


        private IntPtr[] NewTargets(GameObject go)
        {
            var list = new List<IntPtr>();
            list.Add(go.Pointer);
            try
            {
                var comps = go.GetComponents<Component>();
                for (int i = 0; i < comps.Length; i++)
                    if (comps[i] != null) list.Add(comps[i].Pointer);
            }
            catch { }
            return list.ToArray();
        }

        private static IntPtr FindFieldDeep(IntPtr klass, string name)
        {
            IntPtr k = klass;
            int guard = 0;
            while (k != IntPtr.Zero && guard++ < 12)
            {
                IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(k, name);
                if (f != IntPtr.Zero) return f;
                k = IL2CPP.il2cpp_class_get_parent(k);
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindMethodDeep(IntPtr klass, string name, int argc)
        {
            IntPtr k = klass;
            int guard = 0;
            while (k != IntPtr.Zero && guard++ < 12)
            {
                IntPtr m = IL2CPP.il2cpp_class_get_method_from_name(k, name, argc);
                if (m != IntPtr.Zero) return m;
                k = IL2CPP.il2cpp_class_get_parent(k);
            }
            return IntPtr.Zero;
        }

        // 安全版字段导出：只处理游戏自定义类，只读值类型，不 deref 对象指针
        private string DumpFieldsSafe(GameObject go)
        {
            var sb = new StringBuilder();
            sb.Append("{").Append(Qc).Append("ok").Append(Qc).Append(":true,").Append(Qc).Append("target").Append(Qc).Append(":").Append(Qc).Append(JsonEsc(go.name)).Append(Qc);
            sb.Append(",").Append(Qc).Append("note").Append(Qc).Append(":").Append(Qc).Append("only script classes, value types").Append(Qc);
            sb.Append(",").Append(Qc).Append("components").Append(Qc).Append(":[");
            IntPtr[] targets = NewTargets(go);
            int emitted = 0;
            for (int ti = 0; ti < targets.Length; ti++)
            {
                IntPtr op = targets[ti];
                if (op == IntPtr.Zero) continue;
                IntPtr klass = IntPtr.Zero;
                string clsName = "?";
                try
                {
                    klass = IL2CPP.il2cpp_object_get_class(op);
                    clsName = ClassName(klass);
                }
                catch { continue; }
                if (clsName.StartsWith("UnityEngine") || clsName.StartsWith("System") || clsName.StartsWith("Il2Cpp") || clsName.StartsWith("MagicaCloth"))
                {
                    continue;
                }
                if (emitted > 0) sb.Append(',');
                emitted++;
                sb.Append("{").Append(Qc).Append("class").Append(Qc).Append(":").Append(Qc).Append(JsonEsc(clsName)).Append(Qc);
                sb.Append(",").Append(Qc).Append("fields").Append(Qc).Append(":[");
                IntPtr iter = IntPtr.Zero;
                IntPtr field;
                IntPtr k = klass;
                int guardClass = 0;
                int n = 0;
                var seen = new System.Collections.Generic.HashSet<string>();
                while (k != IntPtr.Zero && guardClass++ < 8)
                {
                    iter = IntPtr.Zero;
                    int guardField = 0;
                    while ((field = IL2CPP.il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero && guardField++ < 40 && n < 40)
                    {
                        string fn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                        if (fn == null || seen.Contains(fn)) continue;
                        seen.Add(fn);
                        int off = (int)IL2CPP.il2cpp_field_get_offset(field);
                        if (off == 0) continue;
                        IntPtr ft = IL2CPP.il2cpp_field_get_type(field);
                        int t = (int)IL2CPP.il2cpp_type_get_type(ft);
                        if (t == T_STRING || t == 0x0E || t == 0x12 || t == 0x14 || t == 0x11 || t == 0x0F || t == 0x15 || t == 0x1C)
                        {
                            continue;
                        }
                        string val = "null";
                        try { val = FieldValueJson(field, op); } catch { val = "null"; }
                        if (n > 0) sb.Append(',');
                        n++;
                        sb.Append(Qc).Append(JsonEsc(fn)).Append(Qc).Append(":").Append(val);
                    }
                    k = IL2CPP.il2cpp_class_get_parent(k);
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---------------- tree
        private void TreeAppend(GameObject go, int depth, int maxDepth, StringBuilder sb, int[] counter)
        {
            if (go == null || counter[0] > 800) return;
            counter[0]++;
            for (int i = 0; i < depth; i++) sb.Append("  ");
            sb.Append("- ").Append(JsonEsc(go.name));
            sb.Append(go.activeInHierarchy ? " *" : " .");
            if (depth >= maxDepth) { sb.Append("\n"); return; }
            var tr = go.transform;
            sb.Append(" [").Append(tr.childCount).Append("]\n");
            for (int i = 0; i < tr.childCount; i++)
            {
                GameObject child = null;
                try { child = tr.GetChild(i).gameObject; } catch { }
                if (child != null) TreeAppend(child, depth + 1, maxDepth, sb, counter);
            }
        }

        private string DumpTree(GameObject go, int maxDepth)
        {
            var sb = new StringBuilder();
            var counter = new int[1];
            TreeAppend(go, 0, maxDepth, sb, counter);
            string tree = sb.ToString();
            return "{" + Qc + "ok" + Qc + ":true," + Qc + "target" + Qc + ":" + Qc + JsonEsc(go.name) + Qc
                 + "," + Qc + "nodes" + Qc + ":" + counter[0]
                 + "," + Qc + "tree" + Qc + ":" + Qc + JsonEsc(tree) + Qc + "}";
        }

        // ---------------- get / set field
        private string DoGetField(string body)
        {
            string nm = GetJsonStr(body, "name");
            string fld = GetJsonStr(body, "field");
            if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(fld)) return ErrJson("need name+field");
            return RunOnMain<string>(delegate ()
            {
                GameObject go = FindGO(nm);
                if (go == null) return ErrJson("object not found: " + nm);
                IntPtr[] targets = NewTargets(go);
                for (int ci = 0; ci < targets.Length; ci++)
                {
                    IntPtr op = targets[ci];
                    if (op == IntPtr.Zero) continue;
                    IntPtr klass = IL2CPP.il2cpp_object_get_class(op);
                    IntPtr field = FindFieldDeep(klass, fld);
                    if (field == IntPtr.Zero) continue;
                    string cls = ClassName(klass);
                    string val = FieldValueJson(field, op);
                    return "{" + Qc + "ok" + Qc + ":true," + Qc + "target" + Qc + ":" + Qc + JsonEsc(go.name) + Qc
                         + "," + Qc + "class" + Qc + ":" + Qc + JsonEsc(cls) + Qc
                         + "," + Qc + "field" + Qc + ":" + Qc + JsonEsc(fld) + Qc
                         + "," + Qc + "value" + Qc + ":" + val + "}";
                }
                return ErrJson("field not found: " + fld);
            }, ErrJson("get failed"));
        }

        private string DoSetField(string body)
        {
            string nm = GetJsonStr(body, "name");
            string fld = GetJsonStr(body, "field");
            string val = GetJsonStr(body, "value");
            if (val == null)
            {
                float f = GetJsonNum(body, "value", float.NaN);
                if (!float.IsNaN(f)) val = f.ToString(CultureInfo.InvariantCulture);
            }
            if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(fld) || val == null) return ErrJson("need name+field+value");
            string vv = val;
            return RunOnMain<string>(delegate ()
            {
                GameObject go = FindGO(nm);
                if (go == null) return ErrJson("object not found: " + nm);
                IntPtr[] targets = NewTargets(go);
                for (int ci = 0; ci < targets.Length; ci++)
                {
                    IntPtr op = targets[ci];
                    if (op == IntPtr.Zero) continue;
                    IntPtr klass = IL2CPP.il2cpp_object_get_class(op);
                    IntPtr field = FindFieldDeep(klass, fld);
                    if (field == IntPtr.Zero) continue;
                    WriteFieldValue(field, op, vv);
                    string after = FieldValueJson(field, op);
                    return "{" + Qc + "ok" + Qc + ":true," + Qc + "target" + Qc + ":" + Qc + JsonEsc(go.name) + Qc
                         + "," + Qc + "field" + Qc + ":" + Qc + JsonEsc(fld) + Qc
                         + "," + Qc + "value" + Qc + ":" + after + "}";
                }
                return ErrJson("field not found: " + fld);
            }, ErrJson("set failed"));
        }

        private static unsafe void SafeInvoke(IntPtr method, IntPtr obj)
        {
            void** noArgs = null;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, obj, noArgs, ref exc);
        }

        // ---------------- call method (0~2 个参数，仅数字/字符串)
        private string DoCallMethod(string body)
        {
            string nm = GetJsonStr(body, "name");
            string mth = GetJsonStr(body, "method");
            if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(mth)) return ErrJson("need name+method");
            string a0 = GetJsonStr(body, "arg0");
            string a1 = GetJsonStr(body, "arg1");
            if (a0 == null && !float.IsNaN(GetJsonNum(body, "arg0", float.NaN))) a0 = "num";
            if (a1 == null && !float.IsNaN(GetJsonNum(body, "arg1", float.NaN))) a1 = "num";
            int argc = 0;
            if (a0 != null) argc = 1;
            if (a1 != null) argc = 2;
            return RunOnMain<string>(delegate ()
            {
                GameObject go = FindGO(nm);
                if (go == null) return ErrJson("object not found: " + nm);
                IntPtr[] targets = NewTargets(go);
                for (int ci = 0; ci < targets.Length; ci++)
                {
                    IntPtr op = targets[ci];
                    if (op == IntPtr.Zero) continue;
                    IntPtr klass = IL2CPP.il2cpp_object_get_class(op);
                    IntPtr method = FindMethodDeep(klass, mth, argc);
                    if (method == IntPtr.Zero) continue;
                    int pcount = (int)IL2CPP.il2cpp_method_get_param_count(method);
                    if (pcount != 0) return ErrJson("method needs " + pcount + " args; only 0-arg calls are supported for safety");
                    SafeInvoke(method, op);
                    IntPtr exc = IntPtr.Zero;
                    string cls = ClassName(klass);
                    string ex = exc == IntPtr.Zero ? "null" : Qc + "exception" + Qc;
                    return "{" + Qc + "ok" + Qc + ":true," + Qc + "target" + Qc + ":" + Qc + JsonEsc(go.name) + Qc
                         + "," + Qc + "class" + Qc + ":" + Qc + JsonEsc(cls) + Qc
                         + "," + Qc + "method" + Qc + ":" + Qc + JsonEsc(mth) + Qc
                         + "," + Qc + "argc" + Qc + ":" + argc
                         + "," + Qc + "exception" + Qc + ":" + ex + "}";
                }
                return ErrJson("method not found: " + mth);
            }, ErrJson("call failed"));
        }

        // ---------------- stats
        private string DoStats()
        {
            return RunOnMain<string>(delegate ()
            {
                GameObject[] all = UnityEngine.Object.FindObjectsOfType<GameObject>();
                int active = 0;
                for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].activeInHierarchy) active++;
                var sc = SceneManager.GetActiveScene();
                return "{" + Qc + "ok" + Qc + ":true"
                     + "," + Qc + "objects" + Qc + ":" + all.Length
                     + "," + Qc + "activeObjects" + Qc + ":" + active
                     + "," + Qc + "scene" + Qc + ":" + Qc + JsonEsc(sc.name) + Qc
                     + "," + Qc + "frame" + Qc + ":" + Time.frameCount
                     + "," + Qc + "time" + Qc + ":" + Time.time
                     + "," + Qc + "timeScale" + Qc + ":" + Time.timeScale
                     + "," + Qc + "fps" + Qc + ":" + (int)(1.0f / Math.Max(Time.deltaTime, 0.0001f))
                     + "," + Qc + "unscaledTime" + Qc + ":" + Time.unscaledTime
                     + "," + Qc + "version" + Qc + ":" + Qc + Application.version + Qc + "}";
            }, ErrJson("stats failed"));
        }



        private static string QueryOf(string rawPath, string key)
        {
            int q = rawPath.IndexOf('?');
            if (q < 0) return null;
            string[] kvs = rawPath.Substring(q + 1).Split('&');
            for (int i = 0; i < kvs.Length; i++)
            {
                int eq = kvs[i].IndexOf('=');
                if (eq > 0 && kvs[i].Substring(0, eq) == key) return Uri.UnescapeDataString(kvs[i].Substring(eq + 1));
            }
            return null;
        }

        private static float QueryNum(string rawPath, string key, float def)
        {
            string v = QueryOf(rawPath, key);
            if (string.IsNullOrEmpty(v)) return def;
            float f;
            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            return def;
        }

    }
}
