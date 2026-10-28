using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Minimal edits of Packages/manifest.json without Newtonsoft (not guaranteed to be installed).
    /// The file is parsed into an order-preserving tree and written back with 2-space indentation.
    /// </summary>
    static class SplatManifest
    {
        public const string OpenUpmUrl = "https://package.openupm.com";

        sealed class JObject : List<KeyValuePair<string, object>>
        {
            public int IndexOf(string key)
            {
                for (var i = 0; i < Count; i++) if (this[i].Key == key) return i;
                return -1;
            }
            public object Get(string key) { var i = IndexOf(key); return i < 0 ? null : this[i].Value; }
            public void Set(string key, object value)
            {
                var i = IndexOf(key);
                if (i < 0) Add(new KeyValuePair<string, object>(key, value));
                else this[i] = new KeyValuePair<string, object>(key, value);
            }
        }

        // Numbers, true, false and null are kept verbatim.
        sealed class Raw { public string Text; }

        static string ManifestFile => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages", "manifest.json");
        public static string PackagesDir => Path.GetDirectoryName(ManifestFile);

        /// <summary>Registers <paramref name="scope"/> under the OpenUPM scoped registry. Returns true if the file changed.</summary>
        public static bool EnsureOpenUpmScope(string scope)
        {
            return Edit(root =>
            {
                if (!(root.Get("scopedRegistries") is List<object> regs)) { regs = new List<object>(); root.Set("scopedRegistries", regs); }
                JObject reg = null;
                foreach (var o in regs)
                    if (o is JObject j && j.Get("url") is string url && url.TrimEnd('/') == OpenUpmUrl) { reg = j; break; }
                if (reg == null)
                {
                    reg = new JObject { new("name", "OpenUPM"), new("url", OpenUpmUrl), new("scopes", new List<object>()) };
                    regs.Add(reg);
                }
                if (!(reg.Get("scopes") is List<object> scopes)) { scopes = new List<object>(); reg.Set("scopes", scopes); }
                if (scopes.Contains(scope)) return false;
                scopes.Add(scope);
                return true;
            });
        }

        public static bool SetDependency(string name, string value)
        {
            return Edit(root =>
            {
                if (!(root.Get("dependencies") is JObject deps)) { deps = new JObject(); root.Set("dependencies", deps); }
                if (deps.Get(name) is string old && old == value) return false;
                deps.Set(name, value);
                return true;
            });
        }

        static bool Edit(Func<JObject, bool> change)
        {
            var text = File.ReadAllText(ManifestFile);
            var pos = 0;
            if (!(ParseValue(text, ref pos) is JObject root)) throw new InvalidDataException("Packages/manifest.json is not a JSON object");
            if (!change(root)) return false;
            var sb = new StringBuilder();
            Write(sb, root, 0);
            sb.Append('\n');
            File.WriteAllText(ManifestFile, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        static void Skip(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

        static object ParseValue(string s, ref int p)
        {
            Skip(s, ref p);
            if (p >= s.Length) throw new InvalidDataException("Unexpected end of manifest.json");
            switch (s[p])
            {
                case '{':
                {
                    var o = new JObject();
                    p++; Skip(s, ref p);
                    if (s[p] == '}') { p++; return o; }
                    while (true)
                    {
                        Skip(s, ref p);
                        var key = ParseString(s, ref p);
                        Skip(s, ref p);
                        Expect(s, ref p, ':');
                        o.Add(new KeyValuePair<string, object>(key, ParseValue(s, ref p)));
                        Skip(s, ref p);
                        if (s[p] == ',') { p++; continue; }
                        Expect(s, ref p, '}');
                        return o;
                    }
                }
                case '[':
                {
                    var a = new List<object>();
                    p++; Skip(s, ref p);
                    if (s[p] == ']') { p++; return a; }
                    while (true)
                    {
                        a.Add(ParseValue(s, ref p));
                        Skip(s, ref p);
                        if (s[p] == ',') { p++; continue; }
                        Expect(s, ref p, ']');
                        return a;
                    }
                }
                case '"': return ParseString(s, ref p);
                default:
                {
                    var start = p;
                    while (p < s.Length && !",]}".Contains(s[p]) && !char.IsWhiteSpace(s[p])) p++;
                    if (p == start) throw new InvalidDataException($"Unexpected '{s[p]}' in manifest.json at {p}");
                    return new Raw { Text = s.Substring(start, p - start) };
                }
            }
        }

        static void Expect(string s, ref int p, char c)
        {
            if (p >= s.Length || s[p] != c) throw new InvalidDataException($"Expected '{c}' in manifest.json at {p}");
            p++;
        }

        static string ParseString(string s, ref int p)
        {
            Expect(s, ref p, '"');
            var sb = new StringBuilder();
            while (p < s.Length && s[p] != '"')
            {
                var c = s[p++];
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;
                c = s[p++];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: sb.Append(c); break; // \" \\ \/
                }
            }
            Expect(s, ref p, '"');
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, int depth)
        {
            var pad = new string(' ', (depth + 1) * 2);
            var close = new string(' ', depth * 2);
            switch (v)
            {
                case JObject o when o.Count == 0: sb.Append("{}"); break;
                case JObject o:
                    sb.Append("{\n");
                    for (var i = 0; i < o.Count; i++)
                    {
                        sb.Append(pad); WriteString(sb, o[i].Key); sb.Append(": "); Write(sb, o[i].Value, depth + 1);
                        sb.Append(i < o.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append(close).Append('}');
                    break;
                case List<object> a when a.Count == 0: sb.Append("[]"); break;
                case List<object> a:
                    sb.Append("[\n");
                    for (var i = 0; i < a.Count; i++)
                    {
                        sb.Append(pad); Write(sb, a[i], depth + 1);
                        sb.Append(i < a.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append(close).Append(']');
                    break;
                case string s: WriteString(sb, s); break;
                case Raw r: sb.Append(r.Text); break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
