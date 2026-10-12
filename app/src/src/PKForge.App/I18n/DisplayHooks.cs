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
        var hit = LookupDict(text);
        // 文中の「Pokémon Yellow」などのゲーム名は各言語の正式タイトルに
        return GameTitles.Localize(hit ?? text) ?? hit;
    }

    private static string? LookupDict(string text)
    {
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
    private sealed class FitState
    {
        public Label Label = null!;
        public bool Hooked;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Android.Widget.TextView, FitState> States = new();

    /// <summary>
    /// 行数・幅が決まっているラベルは、レイアウト後に「本当にはみ出しているときだけ」文字を小さくする。
    /// （Android 標準の自動縮小は、文字に合わせて幅が決まるラベルで誤判定して小さくしすぎるため使わない）
    /// </summary>
    private static void Fit(Android.Widget.TextView view, Label label)
    {
        try
        {
            var state = States.GetValue(view, _ => new FitState());
            state.Label = label;
            if (!state.Hooked)
            {
                state.Hooked = true;
                view.LayoutChange += (sender, _) =>
                {
                    if (sender is Android.Widget.TextView tv && States.TryGetValue(tv, out var st))
                        Shrink(tv, st.Label);
                };
            }
            Shrink(view, label);
        }
        catch
        {
            // 縮められなくても表示はそのまま
        }
    }

    private static void Shrink(Android.Widget.TextView view, Label label)
    {
        try
        {
            var bounded = label.MaxLines > 0
                || label.LineBreakMode is LineBreakMode.NoWrap or LineBreakMode.TailTruncation
                    or LineBreakMode.HeadTruncation or LineBreakMode.MiddleTruncation;
            if (!bounded || label.FontSize < 9) return;
            var text = view.Text ?? "";
            var avail = view.Width - view.TotalPaddingLeft - view.TotalPaddingRight;
            if (text.Length == 0 || avail <= 0) return;

            var metrics = view.Resources?.DisplayMetrics;
            if (metrics is null) return;
            var original = Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Sp, (float)label.FontSize, metrics);
            var minimum = original * 0.6f;
            // 短い 1 語（カード見出しなど）は 1 行に収める。それ以外はラベルの行数（NoWrap/省略は 1 行）
            var shortWord = text.Length <= 16 && !text.Trim().Contains(' ');
            var lines = shortWord || label.MaxLines <= 0 ? 1 : label.MaxLines;
            if (shortWord && view.MaxLines != 1) view.SetMaxLines(1);
            else if (!shortWord && label.MaxLines > 0 && view.MaxLines != label.MaxLines) view.SetMaxLines(label.MaxLines);
            else if (!shortWord && label.MaxLines <= 0 && label.LineBreakMode is LineBreakMode.WordWrap or LineBreakMode.CharacterWrap && view.MaxLines == 1)
                view.SetMaxLines(int.MaxValue);

            using var paint = new Android.Text.TextPaint(view.Paint);
            var size = original;
            while (size > minimum && !Fits(paint, text, avail, lines, size)) size -= original * 0.04f;
            if (System.Math.Abs(view.TextSize - size) > 0.5f)
                view.Post(() => view.SetTextSize(Android.Util.ComplexUnitType.Px, size));
        }
        catch
        {
        }
    }

    private static bool Fits(Android.Text.TextPaint paint, string text, int width, int lines, float size)
    {
        paint.TextSize = size;
        if (lines <= 1) return paint.MeasureText(text) <= width;
        if (!System.OperatingSystem.IsAndroidVersionAtLeast(23)) return true;
        using var layout = Android.Text.StaticLayout.Builder.Obtain(text, 0, text.Length, paint, width).Build();
        return layout.LineCount <= lines;
    }
#endif
}
