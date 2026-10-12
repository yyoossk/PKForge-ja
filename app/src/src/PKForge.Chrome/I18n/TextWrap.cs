// PKForge Dex（多言語版）: キャンバスに描く文字の折り返し。
// 幅に収まらない文を「…」で切らずに、決めた行数まで次の行へ送る（最後の行だけ必要なら「…」）。
// 英語などは単語の切れ目で、日本語・中国語・韓国語は文字単位で折り返す（行頭の句読点は避ける）。
#nullable enable
using System.Collections.Generic;
using SkiaSharp;

namespace PKForgeI18n
{
    public static class TextWrap
    {
        private const string NoLineStart = "、。，．・：；？！）」』】〉》〕ー…‥,.:;!?)]}%";

        public static List<string> Lines(string? text, SKFont font, float width, int maxLines)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text) || width <= 0) { result.Add(text ?? ""); return result; }
            var rest = text!;
            while (rest.Length > 0 && result.Count < maxLines)
            {
                if (result.Count == maxLines - 1 || font.MeasureText(rest) <= width)
                {
                    result.Add(Ellipsize(rest, font, width));
                    break;
                }
                var cut = BreakAt(rest, font, width);
                result.Add(rest.Substring(0, cut).TrimEnd());
                rest = rest.Substring(cut).TrimStart();
            }
            if (result.Count == 0) result.Add("");
            return result;
        }

        private static int BreakAt(string s, SKFont font, float width)
        {
            var fit = 1;
            while (fit < s.Length && font.MeasureText(s.Substring(0, fit + 1)) <= width) fit++;
            // 英語などは単語の切れ目まで戻す
            var space = s.LastIndexOf(' ', System.Math.Min(fit, s.Length - 1));
            if (space > fit / 2 && !IsCjk(s[fit - 1])) return space + 1;
            // 行頭に句読点が来ないようにする
            while (fit > 1 && fit < s.Length && NoLineStart.IndexOf(s[fit]) >= 0) fit--;
            return fit;
        }

        private static bool IsCjk(char c) => c >= 0x2E80;

        private static string Ellipsize(string s, SKFont font, float width)
        {
            if (font.MeasureText(s) <= width) return s;
            while (s.Length > 1 && font.MeasureText(s + "…") > width) s = s.Substring(0, s.Length - 1);
            return s.TrimEnd() + "…";
        }
    }
}
