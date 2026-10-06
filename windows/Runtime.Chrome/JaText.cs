// PKForge 日本語化ランタイム。
// パッチャーがこのクラスを PKForge.App.dll に移植し、文字を描く API の呼び出しを
// JaText.T() 経由に書き換える。翻訳は「表示の直前」にだけ行うので、
// アプリ内部の比較・保存データ・設定キーは英語のままで、動作を壊さない。
//
// 注意: アプリの BCL はトリミング済み。ここではごく基本的な API だけを使うこと
// （パッチャーが適用時に、参照しているメンバーが実在するか検証する）。
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PKForgeJa
{
    public static class JaText
    {
        private sealed class Template
        {
            public string[] Lits = null!;      // 英語の固定部分（穴の数 + 1 個）
            public string[] Out = null!;       // 訳文の固定部分
            public int[] Order = null!;        // 訳文に入る穴の番号（Out の間に入る）
            public bool[] Plural = null!;      // 英語の複数形語尾 (s/es) が入る穴
            public int Weight;
        }

        private static readonly object Gate = new object();
        private static Dictionary<string, string>? _exact;
        private static Dictionary<char, List<Template>>? _byFirst;
        private static List<Template>? _noPrefix;
        private static Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static bool _broken;

        public const string ResourceName = "PKForgeJa.table.txt";

        /// <summary>表示用に訳す。失敗したら原文を返す（絶対に例外を外へ出さない）。</summary>
        public static string T(string s)
        {
            if (s == null || s.Length == 0 || _broken) return s!;
            try
            {
                lock (Gate)
                {
                    if (_exact == null) Load();
                    string? hit;
                    if (_cache.TryGetValue(s, out hit)) return hit;
                    var r = Translate(s, 0);
                    if (_cache.Count > 20000) _cache = new Dictionary<string, string>();
                    _cache[s] = r;
                    if (!ReferenceEquals(r, s) && !_cache.ContainsKey(r)) _cache[r] = r;
                    return r;
                }
            }
            catch
            {
                _broken = true;
                return s;
            }
        }

        /// <summary>object 版（バインディング用）。文字列以外は触らない。</summary>
        public static object TObj(object o)
        {
            var s = o as string;
            return s == null ? o : T(s);
        }

        private static void Load()
        {
            var exact = new Dictionary<string, string>();
            var byFirst = new Dictionary<char, List<Template>>();
            var noPrefix = new List<Template>();
            var asm = typeof(JaText).Assembly;
            using (var stream = asm.GetManifestResourceStream(ResourceName))
            {
                if (stream != null)
                {
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length < 3) continue;
                        var f = line.Split('\t');
                        if (f[0] == "E" && f.Length >= 3)
                        {
                            var k = Unescape(f[1]);
                            if (!exact.ContainsKey(k)) exact[k] = Unescape(f[2]);
                        }
                        else if (f[0] == "T" && f.Length >= 3)
                        {
                            var t = Parse(Unescape(f[1]), Unescape(f[2]), f.Length >= 4 ? f[3] : "");
                            if (t == null) continue;
                            if (t.Lits[0].Length == 0) noPrefix.Add(t);
                            else
                            {
                                List<Template>? list;
                                if (!byFirst.TryGetValue(t.Lits[0][0], out list))
                                {
                                    list = new List<Template>();
                                    byFirst[t.Lits[0][0]] = list;
                                }
                                list.Add(t);
                            }
                        }
                    }
                }
            }
            foreach (var list in byFirst.Values) Sort(list);
            Sort(noPrefix);
            _byFirst = byFirst;
            _noPrefix = noPrefix;
            _exact = exact;
        }

        // 固定部分が長い（＝具体的な）テンプレートを先に試す。List.Sort はトリミングで
        // 消えている可能性があるので単純な挿入ソート。
        private static void Sort(List<Template> list)
        {
            for (var i = 1; i < list.Count; i++)
            {
                var x = list[i];
                var j = i - 1;
                while (j >= 0 && list[j].Weight < x.Weight)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = x;
            }
        }

        private static Template? Parse(string key, string value, string plural)
        {
            var lits = new List<string>();
            var holes = new List<int>();
            if (!SplitHoles(key, lits, holes)) return null;
            var n = holes.Count;
            for (var i = 0; i < n; i++)
                if (holes[i] != i) return null;            // 原文側は 0,1,2… の順
            for (var i = 1; i < n; i++)
                if (lits[i].Length == 0) return null;       // 穴が連続すると境界が決まらない
            var outs = new List<string>();
            var order = new List<int>();
            if (!SplitHoles(value, outs, order)) return null;
            foreach (var o in order)
                if (o < 0 || o >= n) return null;
            var t = new Template
            {
                Lits = lits.ToArray(),
                Out = outs.ToArray(),
                Order = order.ToArray(),
                Plural = new bool[n],
            };
            foreach (var p in plural.Split(','))
            {
                int idx;
                if (int.TryParse(p, out idx) && idx >= 0 && idx < n) t.Plural[idx] = true;
            }
            foreach (var l in t.Lits) t.Weight += l.Length;
            return t;
        }

        // "a{0}b{1}c" → lits ["a","b","c"], holes [0,1]
        private static bool SplitHoles(string s, List<string> lits, List<int> holes)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '{')
                {
                    var j = i + 1;
                    var num = 0;
                    var digits = 0;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                    {
                        num = num * 10 + (s[j] - '0');
                        j++;
                        digits++;
                    }
                    if (digits > 0 && j < s.Length && s[j] == '}')
                    {
                        lits.Add(sb.ToString());
                        sb.Length = 0;
                        holes.Add(num);
                        i = j + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            lits.Add(sb.ToString());
            return true;
        }

        private static string Translate(string s, int depth)
        {
            string? hit;
            if (_exact!.TryGetValue(s, out hit)) return hit;

            // 前後の空白を除いて再挑戦
            var a = 0;
            var b = s.Length;
            while (a < b && IsSpace(s[a])) a++;
            while (b > a && IsSpace(s[b - 1])) b--;
            if (a > 0 || b < s.Length)
            {
                if (a == b) return s;
                var core = s.Substring(a, b - a);
                var tc = Translate(core, depth);
                return ReferenceEquals(tc, core) ? s : s.Substring(0, a) + tc + s.Substring(b);
            }

            var r = MatchTemplates(s, depth);
            if (r != null) return r;

            // 複数行・「 · 」区切りは部分ごとに訳す
            if (depth < 3)
            {
                var j = JoinTranslated(s, "\n", depth);
                if (j != null) return j;
                j = JoinTranslated(s, " · ", depth);
                if (j != null) return j;
                j = JoinTranslated(s, " — ", depth);
                if (j != null) return j;
            }
            return s;
        }

        private static bool IsSpace(char c) => c == ' ' || c == '\n' || c == '\r' || c == '\t';

        private static string? JoinTranslated(string s, string sep, int depth)
        {
            if (s.IndexOf(sep) < 0) return null;
            var parts = s.Split(new[] { sep }, System.StringSplitOptions.None);
            var changed = false;
            var sb = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                var t = parts[i].Length == 0 ? parts[i] : Translate(parts[i], depth + 1);
                if (!ReferenceEquals(t, parts[i]) && t != parts[i]) changed = true;
                sb.Append(t);
            }
            return changed ? sb.ToString() : null;
        }

        private static string? MatchTemplates(string s, int depth)
        {
            List<Template>? list;
            if (_byFirst!.TryGetValue(s[0], out list))
            {
                foreach (var t in list)
                {
                    var r = TryTemplate(t, s, depth);
                    if (r != null) return r;
                }
            }
            foreach (var t in _noPrefix!)
            {
                var r = TryTemplate(t, s, depth);
                if (r != null) return r;
            }
            return null;
        }

        private static string? TryTemplate(Template t, string s, int depth)
        {
            var lits = t.Lits;
            var n = lits.Length - 1;
            var first = lits[0];
            var last = lits[n];
            if (s.Length < t.Weight) return null;
            if (!StartsAt(s, first, 0)) return null;
            var lastStart = s.Length - last.Length;
            if (!StartsAt(s, last, lastStart)) return null;
            var caps = new string[n];
            var pos = first.Length;
            for (var i = 1; i < n; i++)
            {
                var idx = IndexFrom(s, lits[i], pos, lastStart);
                if (idx < 0) return null;
                caps[i - 1] = s.Substring(pos, idx - pos);
                pos = idx + lits[i].Length;
            }
            if (pos > lastStart) return null;
            caps[n - 1] = s.Substring(pos, lastStart - pos);

            var sb = new StringBuilder();
            for (var i = 0; i < t.Out.Length; i++)
            {
                sb.Append(t.Out[i]);
                if (i < t.Order.Length)
                {
                    var h = t.Order[i];
                    var v = caps[h];
                    if (t.Plural[h] && (v.Length == 0 || v == "s" || v == "es")) continue;
                    sb.Append(depth < 3 && v.Length > 0 ? Translate(v, depth + 1) : v);
                }
            }
            return sb.ToString();
        }

        private static bool StartsAt(string s, string part, int at)
        {
            if (at < 0 || at + part.Length > s.Length) return false;
            for (var i = 0; i < part.Length; i++)
                if (s[at + i] != part[i]) return false;
            return true;
        }

        private static int IndexFrom(string s, string part, int from, int limit)
        {
            for (var i = from; i + part.Length <= limit; i++)
                if (StartsAt(s, part, i)) return i;
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    var d = s[++i];
                    sb.Append(d == 'n' ? '\n' : d == 't' ? '\t' : d == 'r' ? '\r' : d);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
