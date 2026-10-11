// PKForge Dex（多言語版）: 表示まわりの共通フック。
//  1. 比較に使われていてソース上は英語のまま残した文字列を、表示の直前にだけ訳す
//  2. 行数・幅が決まっているラベルは、長い訳文でも見切れないように文字を自動で縮める
#nullable enable
using Microsoft.Maui.Controls;

namespace PKForgeI18n;

internal static class DisplayHooks
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
#if ANDROID
        // MAUI は最初の Label / Button が作られたときに自分の対応表を差し替える（後から足した処理が消える）。
        // 先にダミーを作って差し替えを済ませてから足す。
        try
        {
            _ = new Label();
            _ = new Button();
        }
        catch
        {
        }
        Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("Text", (handler, view) =>
        {
            if (view is not Label label) return;
            if (label.FormattedText is null && Lookup(label.Text) is { } text)
                handler.PlatformView.Text = text;
            Fit(handler.PlatformView, label);
        });
        Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("Text", (handler, view) =>
        {
            if (view is Button button && Lookup(button.Text) is { } text)
                handler.PlatformView.Text = text;
        });
        foreach (var key in new[] { "Font", "MaxLines", "LineBreakMode" })
            Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping(key, (handler, view) =>
            {
                if (view is Label label) Fit(handler.PlatformView, label);
            });
#endif
    }

    /// <summary>表示用の訳（無ければ null）</summary>
    public static string? Lookup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lang = LangSetup.Current;
        if (lang <= 0 || lang >= DisplayDict.ByLang.Length) return null;
        var dict = DisplayDict.ByLang[lang];
        if (dict is not null && dict.TryGetValue(text, out var hit)) return hit;
        // 「Generation: すべて」のような前置き＋値
        var prefixes = DisplayDict.PrefixByLang[lang];
        if (prefixes is null || prefixes.Count == 0) return null;
        foreach (var pair in prefixes)
            if (text.Length > pair.Key.Length && text.StartsWith(pair.Key, System.StringComparison.Ordinal))
                return pair.Value + text.Substring(pair.Key.Length);
        return null;
    }

#if ANDROID
    private static void Fit(Android.Widget.TextView view, Label label)
    {
        try
        {
            var bounded = label.MaxLines > 0
                || label.LineBreakMode is LineBreakMode.NoWrap or LineBreakMode.TailTruncation
                    or LineBreakMode.HeadTruncation or LineBreakMode.MiddleTruncation;
            var max = (int)System.Math.Round(label.FontSize);
            if (!bounded || max < 9)
            {
                AndroidX.Core.Widget.TextViewCompat.SetAutoSizeTextTypeWithDefaults(view, AndroidX.Core.Widget.TextViewCompat.AutoSizeTextTypeNone);
                return;
            }
            var min = System.Math.Max(8, (int)(max * 0.62));
            // 短い 1 語（カードの見出しなど）は途中で改行させず、1 行に収まるまで縮める
            var text = view.Text ?? "";
            var shortWord = text.Length is > 0 and <= 16 && !text.Trim().Contains(' ');
            if (shortWord && label.MaxLines != 1) view.SetMaxLines(1);
            else if (label.MaxLines > 0) view.SetMaxLines(label.MaxLines);
            AndroidX.Core.Widget.TextViewCompat.SetAutoSizeTextTypeUniformWithConfiguration(
                view, min, max, 1, (int)Android.Util.ComplexUnitType.Sp);
        }
        catch
        {
            // 縮められなくても表示はそのまま
        }
    }
#endif
}
