using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MiniMcp
{
    public partial class MiniMcpPlugin
    {
        private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private object ResolveStart(GameObject go, string compName)
        {
            if (go == null) return null;
            if (string.IsNullOrEmpty(compName))
            {
                Component[] cs = go.GetComponents<Component>();
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] != null && !(cs[i] is Transform)) return cs[i];
                }
                return null;
            }
            return ResolveComponent(go, compName);
        }

        private static object GetMemberValue(object obj, string name, out string err)
        {
            err = null;
            if (obj == null) { err = "target is null"; return null; }
            Type t = obj.GetType();
            FieldInfo f = t.GetField(name, MemberFlags);
            if (f != null) return f.GetValue(obj);
            PropertyInfo p = t.GetProperty(name, MemberFlags);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null);
            err = "member not found: " + name + " on " + t.Name;
            return null;
        }

        private static bool SetMemberValue(object obj, string name, JToken value, out string err)
        {
            err = null;
            if (obj == null) { err = "target is null"; return false; }
            Type t = obj.GetType();
            FieldInfo f = t.GetField(name, MemberFlags);
            if (f != null)
            {
                f.SetValue(obj, value == null ? null : value.ToObject(f.FieldType));
                return true;
            }
            PropertyInfo p = t.GetProperty(name, MemberFlags);
            if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0)
            {
                p.SetValue(obj, value == null ? null : value.ToObject(p.PropertyType), null);
                return true;
            }
            err = "writable member not found: " + name + " on " + t.Name;
            return false;
        }

        private static List<string> SplitPath(string path)
        {
            List<string> parts = new List<string>();
            if (string.IsNullOrEmpty(path)) return parts;
            int start = 0;
            int depth = 0;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '[') depth++;
                else if (c == ']') { if (depth > 0) depth--; }
                else if (c == '.' && depth == 0)
                {
                    parts.Add(path.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(path.Substring(start));
            return parts;
        }

        private static object IndexObject(object obj, int index, out string err)
        {
            err = null;
            if (obj == null) { err = "null before index"; return null; }
            Array arr = obj as Array;
            if (arr != null)
            {
                if (index < 0 || index >= arr.Length) { err = "array index out of range"; return null; }
                return arr.GetValue(index);
            }
            IList list = obj as IList;
            if (list != null)
            {
                if (index < 0 || index >= list.Count) { err = "list index out of range"; return null; }
                return list[index];
            }
            err = "not indexable: " + obj.GetType().Name;
            return null;
        }

        private static object WalkPath(object root, string path, out string err)
        {
            err = null;
            object cur = root;
            List<string> parts = SplitPath(path);
            for (int i = 0; i < parts.Count; i++)
            {
                string seg = parts[i];
                if (string.IsNullOrEmpty(seg)) continue;
                string name = seg;
                List<int> idx = new List<int>();
                int lb = seg.IndexOf('[');
                if (lb >= 0)
                {
                    name = seg.Substring(0, lb);
                    int p = lb;
                    while (p < seg.Length)
                    {
                        int o = seg.IndexOf('[', p);
                        if (o < 0) break;
                        int c = seg.IndexOf(']', o);
                        if (c < 0) break;
                        int parsed;
                        if (int.TryParse(seg.Substring(o + 1, c - o - 1), out parsed)) idx.Add(parsed);
                        p = c + 1;
                    }
                }
                if (!string.IsNullOrEmpty(name))
                {
                    string e1;
                    cur = GetMemberValue(cur, name, out e1);
                    if (e1 != null) { err = e1; return null; }
                }
                for (int k = 0; k < idx.Count; k++)
                {
                    string e2;
                    cur = IndexObject(cur, idx[k], out e2);
                    if (e2 != null) { err = e2; return null; }
                }
                if (cur == null) { err = "null at segment: " + seg; return null; }
            }
            return cur;
        }

        private static bool WritePath(object root, string path, JToken value, out string err)
        {
            err = null;
            List<string> parts = SplitPath(path);
            if (parts.Count == 0) { err = "empty path"; return false; }
            string last = parts[parts.Count - 1];
            string lastName = last;
            int lb = last.IndexOf('[');
            if (lb >= 0) lastName = last.Substring(0, lb);
            object parent = root;
            if (parts.Count > 1)
            {
                string pre = path.Substring(0, path.Length - last.Length);
                parent = WalkPath(root, pre, out err);
                if (err != null) return false;
            }
            string e;
            if (!SetMemberValue(parent, lastName, value, out e)) { err = e; return false; }
            return true;
        }

        private string GetPathJson(Dictionary<string, string> q)
        {
            int id = GetInt(q, "id", 0);
            string compName = GetStr(q, "component");
            string path = GetStr(q, "path");
            if (string.IsNullOrEmpty(path)) return JsonConvert.SerializeObject(new { ok = false, error = "path required" });
            GameObject go = FindById(id);
            if (go == null) return JsonConvert.SerializeObject(new { ok = false, error = "gameobject not found: " + id });
            object start = ResolveStart(go, compName);
            if (start == null) return JsonConvert.SerializeObject(new { ok = false, error = "start not found" });
            string err;
            object v = WalkPath(start, path, out err);
            if (err != null) return JsonConvert.SerializeObject(new { ok = false, error = err, path = path });
            return JsonConvert.SerializeObject(new { ok = true, path = path, type = v == null ? "null" : v.GetType().Name, value = DescribeValue(v) });
        }

        private string SetPathJson(string body)
        {
            if (string.IsNullOrEmpty(body)) return JsonConvert.SerializeObject(new { ok = false, error = "empty body" });
            JObject jo = JObject.Parse(body);
            int id = jo.Value<int>("id");
            string compName = jo.Value<string>("component");
            string path = jo.Value<string>("path");
            JToken value = jo["value"];
            if (string.IsNullOrEmpty(path)) return JsonConvert.SerializeObject(new { ok = false, error = "path required" });
            GameObject go = FindById(id);
            if (go == null) return JsonConvert.SerializeObject(new { ok = false, error = "gameobject not found: " + id });
            object start = ResolveStart(go, compName);
            if (start == null) return JsonConvert.SerializeObject(new { ok = false, error = "start not found" });
            string err;
            if (!WritePath(start, path, value, out err)) return JsonConvert.SerializeObject(new { ok = false, error = err, path = path });
            string err2;
            object after = WalkPath(start, path, out err2);
            return JsonConvert.SerializeObject(new { ok = true, path = path, now = err2 == null ? DescribeValue(after) : "readback failed" });
        }

        private string FieldsJson(Dictionary<string, string> q)
        {
            int id = GetInt(q, "id", 0);
            string compName = GetStr(q, "component");
            string path = GetStr(q, "path");
            int limit = GetInt(q, "limit", 40);
            GameObject go = FindById(id);
            if (go == null) return JsonConvert.SerializeObject(new { ok = false, error = "gameobject not found: " + id });
            object target = ResolveStart(go, compName);
            if (target == null) return JsonConvert.SerializeObject(new { ok = false, error = "start not found" });
            string err = null;
            if (!string.IsNullOrEmpty(path))
            {
                target = WalkPath(target, path, out err);
                if (err != null) return JsonConvert.SerializeObject(new { ok = false, error = err });
            }
            if (target == null) return JsonConvert.SerializeObject(new { ok = false, error = "target is null" });
            return JsonConvert.SerializeObject(new { ok = true, type = target.GetType().FullName, members = DescribeMembers(target, limit) });
        }

        private static List<object> DescribeMembers(object obj, int limit)
        {
            List<object> list = new List<object>();
            if (obj == null) return list;
            Type t = obj.GetType();
            FieldInfo[] fis = t.GetFields(MemberFlags);
            for (int i = 0; i < fis.Length && list.Count < limit; i++)
            {
                object v = null;
                try { v = fis[i].GetValue(obj); } catch (Exception) { continue; }
                list.Add(new { kind = "field", name = fis[i].Name, type = fis[i].FieldType.Name, value = DescribeValue(v) });
            }
            PropertyInfo[] pis = t.GetProperties(MemberFlags);
            for (int i = 0; i < pis.Length && list.Count < limit; i++)
            {
                if (!pis[i].CanRead || pis[i].GetIndexParameters().Length > 0) continue;
                object v = null;
                try { v = pis[i].GetValue(obj, null); } catch (Exception) { continue; }
                list.Add(new { kind = "prop", name = pis[i].Name, type = pis[i].PropertyType.Name, value = DescribeValue(v) });
            }
            return list;
        }

        private static bool IsNumMatch(object v, double target)
        {
            if (v == null) return false;
            Type t = v.GetType();
            if (t == typeof(int)) return (double)(int)v == target;
            if (t == typeof(long)) return (double)(long)v == target;
            if (t == typeof(short)) return (double)(short)v == target;
            if (t == typeof(byte)) return (double)(byte)v == target;
            if (t == typeof(uint)) return (double)(uint)v == target;
            if (t == typeof(float)) return Math.Abs((float)v - target) < 0.001;
            if (t == typeof(double)) return Math.Abs((double)v - target) < 0.001;
            if (t == typeof(decimal)) return Math.Abs((double)(decimal)v - target) < 0.001;
            return false;
        }

        private string ScanJson(Dictionary<string, string> q)
        {
            string sv = GetStr(q, "value");
            double target;
            if (sv == null || !double.TryParse(sv, out target)) return JsonConvert.SerializeObject(new { ok = false, error = "value required (number)" });
            string typeFilter = GetStr(q, "type");
            int max = GetInt(q, "max", 30);
            int maxObjects = GetInt(q, "maxobjects", 600);
            List<object> hits = new List<object>();
            MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            for (int i = 0; i < all.Length && i < maxObjects; i++)
            {
                MonoBehaviour mb = all[i];
                if (mb == null) continue;
                string tn = mb.GetType().Name;
                if (!string.IsNullOrEmpty(typeFilter) && tn.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                FieldInfo[] fis = mb.GetType().GetFields(MemberFlags);
                for (int j = 0; j < fis.Length; j++)
                {
                    object v = null;
                    try { v = fis[j].GetValue(mb); } catch (Exception) { continue; }
                    if (IsNumMatch(v, target))
                    {
                        hits.Add(new { comp = tn, go = mb.gameObject.GetInstanceID(), path = fis[j].Name, value = DescribeValue(v), depth = 1 });
                    }
                    else if (v != null && !(v is UnityEngine.Object) && !(v is string) && !v.GetType().IsPrimitive && !v.GetType().IsEnum && !(v is IEnumerable))
                    {
                        FieldInfo[] sub = null;
                        try { sub = v.GetType().GetFields(MemberFlags); } catch (Exception) { sub = null; }
                        if (sub != null)
                        {
                            for (int k = 0; k < sub.Length; k++)
                            {
                                object v2 = null;
                                try { v2 = sub[k].GetValue(v); } catch (Exception) { continue; }
                                if (IsNumMatch(v2, target))
                                {
                                    hits.Add(new { comp = tn, go = mb.gameObject.GetInstanceID(), path = fis[j].Name + "." + sub[k].Name, value = DescribeValue(v2), depth = 2 });
                                    if (hits.Count >= max) break;
                                }
                            }
                        }
                    }
                    if (hits.Count >= max) break;
                }
                if (hits.Count >= max) break;
            }
            return JsonConvert.SerializeObject(new { ok = true, target = target, count = hits.Count, hits = hits });
        }
    }
}
