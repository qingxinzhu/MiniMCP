using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiniMcp
{
    [BepInPlugin("com.operit.minimcp", "MiniMCP Bridge", "0.1.0")]
    public partial class MiniMcpPlugin : BaseUnityPlugin
    {
        private ConfigEntry<int> cfgPort;
        private ConfigEntry<string> cfgBind;
        private TcpListener listener;
        private volatile bool running;
        private static MiniMcpPlugin instance;
        private readonly ConcurrentQueue<Action> mainJobs = new ConcurrentQueue<Action>();

        private void Awake()
        {
            instance = this;
            cfgPort = Config.Bind("Network", "Port", 18081, "TCP listen port");
            cfgBind = Config.Bind("Network", "BindAddress", "0.0.0.0", "Bind address");
            try
            {
                IPAddress addr = IPAddress.Parse(cfgBind.Value);
                listener = new TcpListener(addr, cfgPort.Value);
                listener.Start();
                running = true;
                Thread th = new Thread(AcceptLoop);
                th.IsBackground = true;
                th.Name = "MiniMcpAccept";
                th.Start();
                Logger.LogInfo("MiniMCP listening on " + cfgBind.Value + ":" + cfgPort.Value);
            }
            catch (Exception e)
            {
                Logger.LogError("MiniMCP start failed: " + e);
            }
        }

        private void Update()
        {
            for (int i = 0; i < 32; i++)
            {
                Action job;
                if (!mainJobs.TryDequeue(out job)) return;
                try { job(); }
                catch (Exception e) { Logger.LogError("MiniMCP job error: " + e); }
            }
        }

        private void OnDestroy()
        {
            running = false;
            try { if (listener != null) listener.Stop(); }
            catch (Exception) { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client = null;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { if (!running) return; continue; }
                TcpClient c = client;
                Thread th = new Thread(() => HandleClient(c));
                th.IsBackground = true;
                th.Start();
            }
        }

        private static string RunOnMain(Func<string> fn, int timeoutMs)
        {
            string result = null;
            Exception err = null;
            ManualResetEventSlim done = new ManualResetEventSlim(false);
            instance.mainJobs.Enqueue(delegate {
                try { result = fn(); }
                catch (Exception e) { err = e; }
                finally { done.Set(); }
            });
            if (!done.Wait(timeoutMs)) throw new TimeoutException("main thread busy");
            if (err != null) throw err;
            return result;
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    NetworkStream ns = client.GetStream();
                    ns.ReadTimeout = 15000;
                    ns.WriteTimeout = 15000;
                    string head = ReadHead(ns);
                    if (string.IsNullOrEmpty(head)) return;
                    string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' ');
                    string method = first.Length > 0 ? first[0].ToUpperInvariant() : "GET";
                    string url = first.Length > 1 ? first[1] : "/";
                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int idx = lines[i].IndexOf(':');
                        if (idx > 0) headers[lines[i].Substring(0, idx).Trim()] = lines[i].Substring(idx + 1).Trim();
                    }
                    int len = 0;
                    string lenStr;
                    if (headers.TryGetValue("Content-Length", out lenStr)) int.TryParse(lenStr, out len);
                    string body = "";
                    if (len > 0)
                    {
                        byte[] buf = new byte[len];
                        int got = 0;
                        while (got < len)
                        {
                            int n = ns.Read(buf, got, len - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        body = Encoding.UTF8.GetString(buf, 0, got);
                    }
                    string json;
                    try { json = Route(method, url, body); }
                    catch (Exception ex) { json = JsonConvert.SerializeObject(new { ok = false, error = ex.Message }); }
                    WriteResponse(ns, json);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("MiniMCP client error: " + e.Message);
            }
        }

        private static string ReadHead(Stream s)
        {
            List<byte> buf = new List<byte>(2048);
            int b;
            while ((b = s.ReadByte()) != -1)
            {
                buf.Add((byte)b);
                int n = buf.Count;
                if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10)
                    return Encoding.UTF8.GetString(buf.ToArray());
                if (n > 65536) break;
            }
            return null;
        }

        private static void WriteResponse(Stream s, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(payload, 0, payload.Length);
            s.Flush();
        }

        private string Route(string method, string url, string body)
        {
            string path = url;
            string query = "";
            int q = url.IndexOf('?');
            if (q >= 0) { path = url.Substring(0, q); query = url.Substring(q + 1); }
            Dictionary<string, string> qs = ParseQuery(query);
            path = path.TrimEnd('/');
            if (path.Length == 0) path = "/health";
            switch (path)
            {
                case "/health":
                    return RunOnMain(() => JsonConvert.SerializeObject(new { ok = true, plugin = "MiniMCP", unity = Application.unityVersion, scene = SceneManager.GetActiveScene().name }), 8000);
                case "/tree":
                    return RunOnMain(() => TreeJson(qs), 10000);
                case "/find":
                    return RunOnMain(() => FindJson(qs), 20000);
                case "/inspect":
                    return RunOnMain(() => InspectJson(qs), 10000);
                case "/set":
                    return RunOnMain(() => SetJson(body), 10000);
                case "/call":
                    return RunOnMain(() => CallJson(body), 10000);
                case "/getpath":
                    return RunOnMain(() => GetPathJson(qs), 10000);
                case "/setpath":
                    return RunOnMain(() => SetPathJson(body), 10000);
                case "/fields":
                    return RunOnMain(() => FieldsJson(qs), 15000);
                case "/scan":
                    return RunOnMain(() => ScanJson(qs), 30000);
                default:
                    return JsonConvert.SerializeObject(new { ok = false, error = "unknown path: " + path });
            }
        }

        private static Dictionary<string, string> ParseQuery(string q)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return d;
            string[] parts = q.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                int eq = parts[i].IndexOf('=');
                if (eq < 0) d[Uri.UnescapeDataString(parts[i])] = "";
                else d[Uri.UnescapeDataString(parts[i].Substring(0, eq))] = Uri.UnescapeDataString(parts[i].Substring(eq + 1));
            }
            return d;
        }

        private static int GetInt(Dictionary<string, string> q, string key, int def)
        {
            string s;
            int v;
            if (q.TryGetValue(key, out s) && int.TryParse(s, out v)) return v;
            return def;
        }

        private static string GetStr(Dictionary<string, string> q, string key)
        {
            string s;
            return q.TryGetValue(key, out s) ? s : null;
        }

        private static string GetPath(Transform t)
        {
            StringBuilder sb = new StringBuilder();
            while (t != null)
            {
                sb.Insert(0, "/" + t.name + "[" + t.GetSiblingIndex() + "]");
                t = t.parent;
            }
            return sb.ToString();
        }

        private static object NodeInfo(Transform t, int depth, int level)
        {
            List<string> comps = new List<string>();
            Component[] cs = t.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i] != null) comps.Add(cs[i].GetType().Name);
            }
            object[] children = null;
            if (level < depth)
            {
                List<object> list = new List<object>();
                for (int i = 0; i < t.childCount; i++) list.Add(NodeInfo(t.GetChild(i), depth, level + 1));
                children = list.ToArray();
            }
            return new
            {
                name = t.name,
                id = t.gameObject.GetInstanceID(),
                active = t.gameObject.activeSelf,
                path = GetPath(t),
                components = comps.ToArray(),
                children = children
            };
        }

        private string TreeJson(Dictionary<string, string> q)
        {
            int depth = GetInt(q, "depth", 2);
            int max = GetInt(q, "max", 200);
            List<object> roots = new List<object>();
            Scene scene = SceneManager.GetActiveScene();
            GameObject[] gos = scene.GetRootGameObjects();
            for (int i = 0; i < gos.Length; i++)
            {
                if (roots.Count >= max) break;
                roots.Add(NodeInfo(gos[i].transform, depth, 1));
            }
            return JsonConvert.SerializeObject(new { ok = true, scene = scene.name, roots = roots });
        }

        private static GameObject FindById(int id)
        {
            Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.GetInstanceID() == id) return all[i].gameObject;
            }
            return null;
        }

        private string FindJson(Dictionary<string, string> q)
        {
            string name = GetStr(q, "name");
            string comp = GetStr(q, "component");
            int max = GetInt(q, "max", 50);
            List<object> results = new List<object>();
            Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                GameObject go = all[i].gameObject;
                if (name != null && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (comp != null)
                {
                    bool has = false;
                    Component[] cs = go.GetComponents<Component>();
                    for (int j = 0; j < cs.Length; j++)
                    {
                        if (cs[j] == null) continue;
                        if (cs[j].GetType().Name.IndexOf(comp, StringComparison.OrdinalIgnoreCase) >= 0) { has = true; break; }
                    }
                    if (!has) continue;
                }
                results.Add(new { name = go.name, id = go.GetInstanceID(), active = go.activeInHierarchy, path = GetPath(all[i]) });
                if (results.Count >= max) break;
            }
            return JsonConvert.SerializeObject(new { ok = true, count = results.Count, results = results });
        }

        private static object DescribeValue(object v)
        {
            if (v == null) return null;
            Type t = v.GetType();
            if (t.IsPrimitive || v is string || v is decimal) return v;
            if (t.IsEnum) return v.ToString();
            UnityEngine.Object uo = v as UnityEngine.Object;
            if (uo != null) return "<" + t.Name + ": " + uo.name + ">";
            return "<" + t.Name + ">";
        }

        private string InspectJson(Dictionary<string, string> q)
        {
            int id = GetInt(q, "id", 0);
            int limit = GetInt(q, "limit", 24);
            GameObject go = FindById(id);
            if (go == null) return JsonConvert.SerializeObject(new { ok = false, error = "gameobject not found: " + id });
            List<object> compList = new List<object>();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Component[] cs = go.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                Component c = cs[i];
                if (c == null) continue;
                List<object> members = new List<object>();
                Type ct = c.GetType();
                FieldInfo[] fis = ct.GetFields(flags);
                for (int j = 0; j < fis.Length && members.Count < limit; j++)
                {
                    object val = null;
                    try { val = fis[j].GetValue(c); } catch (Exception) { }
                    members.Add(new { kind = "field", name = fis[j].Name, type = fis[j].FieldType.Name, value = DescribeValue(val) });
                }
                PropertyInfo[] pis = ct.GetProperties(flags);
                for (int j = 0; j < pis.Length && members.Count < limit; j++)
                {
                    if (!pis[j].CanRead || pis[j].GetIndexParameters().Length > 0) continue;
                    object val = null;
                    try { val = pis[j].GetValue(c, null); } catch (Exception) { }
                    members.Add(new { kind = "prop", name = pis[j].Name, type = pis[j].PropertyType.Name, value = DescribeValue(val) });
                }
                compList.Add(new { type = ct.FullName, shortType = ct.Name, members = members });
            }
            return JsonConvert.SerializeObject(new { ok = true, id = id, name = go.name, path = GetPath(go.transform), active = go.activeInHierarchy, components = compList });
        }

        private static Component ResolveComponent(GameObject go, string spec)
        {
            Component[] cs = go.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i] == null) continue;
                if (spec == null) return cs[i];
                Type t = cs[i].GetType();
                if (t.Name == spec || t.FullName == spec) return cs[i];
            }
            if (spec != null)
            {
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] == null) continue;
                    if (cs[i].GetType().Name.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0) return cs[i];
                }
            }
            return null;
        }

        private string SetJson(string body)
        {
            if (string.IsNullOrEmpty(body)) return JsonConvert.SerializeObject(new { ok = false, error = "empty body" });
            JObject jo = JObject.Parse(body);
            int id = jo.Value<int>("id");
            string compName = jo.Value<string>("component");
            string member = jo.Value<string>("member");
            JToken valueToken = jo["value"];
            GameObject go = FindById(id);
            if (go == null) return JsonConvert.SerializeObject(new { ok = false, error = "gameobject not found" });
            Component comp = ResolveComponent(go, compName);
            if (comp == null) return JsonConvert.SerializeObject(new { ok = false, error = "component not found: " + compName });
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type ct = comp.GetType();
            FieldInfo fi = ct.GetField(member, flags);
            if (fi != null)
            {
                object v = valueToken == null ? null : valueToken.ToObject(fi.FieldType);
                fi.SetValue(comp, v);
                return JsonConvert.SerializeObject(new { ok = true, set = "field", name = member, value = DescribeValue(fi.GetValue(comp)) });
            }
            PropertyInfo pi = ct.GetProperty(member, flags);
            if (pi != null && pi.CanWrite)
            {
                object v = valueToken == null ? null : valueToken.ToObject(pi.PropertyType);
                pi.SetValue(comp, v, null);
                return JsonConvert.SerializeObject(new { ok = true, set = "prop", name = member, value = DescribeValue(pi.GetValue(comp, null)) });
            }
            return JsonConvert.SerializeObject(new { ok = false, error = "member not found: " + member });
        }

        private string CallJson(string body)
        {
            if (string.IsNullOrEmpty(body)) return JsonConvert.SerializeObject(new { ok = false, error = "empty body" });
            JObject jo = JObject.Parse(body);
            int id = jo.Value<int>("id");
            string compName = jo.Value<string>("component");
            string methodName = jo.Value<string>("method");
            JArray argsToken = jo["args"] as JArray;
            GameObject go = FindById(id);
            if (go == null) return JsonConvert.SerializeObject(new { ok = false, error = "gameobject not found" });
            Component comp = ResolveComponent(go, compName);
            if (comp == null) return JsonConvert.SerializeObject(new { ok = false, error = "component not found: " + compName });
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo[] mis = comp.GetType().GetMethods(flags);
            for (int i = 0; i < mis.Length; i++)
            {
                MethodInfo mi = mis[i];
                if (mi.Name != methodName) continue;
                ParameterInfo[] ps = mi.GetParameters();
                int argCount = argsToken == null ? 0 : argsToken.Count;
                if (ps.Length != argCount) continue;
                object[] args = new object[ps.Length];
                bool ok = true;
                for (int j = 0; j < ps.Length; j++)
                {
                    try { args[j] = argsToken[j].ToObject(ps[j].ParameterType); }
                    catch (Exception) { ok = false; break; }
                }
                if (!ok) continue;
                object ret = mi.Invoke(comp, args);
                return JsonConvert.SerializeObject(new { ok = true, method = methodName, result = DescribeValue(ret) });
            }
            return JsonConvert.SerializeObject(new { ok = false, error = "method not found or arg mismatch: " + methodName });
        }
    }
}