// PKForge Dex（多言語版）: どうぐ名は画面では表示言語で出し、アイコン（英語名から作るファイル名）や
// 英語名で照合する処理のときだけ英語名に戻す。PKHeX の各言語のどうぐ表を番号で突き合わせて対応表を作る。
#nullable enable
using System.Collections.Generic;
using PKHeX.Core;

namespace PKForgeI18n;

public static class ItemL10n
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _toEnglish;

    /// <summary>表示言語のどうぐ名 → 英語名（知らない名前・英語のときはそのまま）。</summary>
    public static string ToEnglish(string name)
    {
        if (string.IsNullOrEmpty(name) || LangSetup.Current == 0) return name;
        var map = _toEnglish;
        if (map is null)
            lock (Gate)
                map = _toEnglish ??= Build();
        return map.TryGetValue(name, out var en) ? en : name;
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        try
        {
            var local = GameInfo.Strings;
            var en = GameInfo.GetStrings("en");
            // 世代ごとのどうぐ表（g1items など）も含め、名前に item が付く文字列配列をすべて突き合わせる
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(GameStrings);
            var lists = new List<(string[]? L, string[]? E)>
            {
                (local.itemlist, en.itemlist),
            };
            foreach (var field in type.GetFields(flags))
                if (field.FieldType == typeof(string[]) && field.Name.Contains("item", System.StringComparison.OrdinalIgnoreCase))
                    lists.Add((field.GetValue(local) as string[], field.GetValue(en) as string[]));
            foreach (var prop in type.GetProperties(flags))
                if (prop.PropertyType == typeof(string[]) && prop.GetIndexParameters().Length == 0
                    && prop.Name.Contains("item", System.StringComparison.OrdinalIgnoreCase))
                {
                    try { lists.Add((prop.GetValue(local) as string[], prop.GetValue(en) as string[])); }
                    catch { }
                }
            foreach (var (l, e) in lists)
            {
                if (l is null || e is null) continue;
                var n = System.Math.Min(l.Length, e.Length);
                for (var i = 0; i < n; i++)
                    if (!string.IsNullOrEmpty(l[i]) && !string.IsNullOrEmpty(e[i]) && l[i] != e[i])
                        map.TryAdd(l[i], e[i]);
            }
        }
        catch
        {
        }
        return map;
    }
}
