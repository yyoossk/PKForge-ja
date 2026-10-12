// PKForge Dex（多言語版）: キャンバスに描く文字のフォント代替。
// 本家のピクセルフォントには韓国語・中国語の一部などが無く「□」になるので、
// 描く文字列に足りない文字があれば、その文字を持つ端末のフォントに切り替える。
#nullable enable
using System.Collections.Generic;
using SkiaSharp;

namespace PKForgeI18n
{
    public static class FontFallback
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<int, SKTypeface> ByChar = new();
        private static readonly Dictionary<(SKTypeface, float, bool, SKFontEdging), SKFont> Fonts = new();

        public static SKFont Covering(SKFont font, string? text)
        {
            if (string.IsNullOrEmpty(text) || font.Typeface is not { } face) return font;
            try
            {
                var missing = -1;
                foreach (var ch in text!)
                {
                    if (char.IsWhiteSpace(ch) || char.IsSurrogate(ch)) continue;
                    if (face.GetGlyph(ch) == 0) { missing = ch; break; }
                }
                if (missing < 0) return font;
                lock (Gate)
                {
                    if (!ByChar.TryGetValue(missing, out var alt))
                        ByChar[missing] = alt = SKFontManager.Default.MatchCharacter(missing) ?? SKTypeface.Default;
                    if (ReferenceEquals(alt, face)) return font;
                    var key = (alt, font.Size, font.Embolden, font.Edging);
                    if (!Fonts.TryGetValue(key, out var made))
                        Fonts[key] = made = new SKFont(alt, font.Size) { Embolden = font.Embolden, Edging = font.Edging };
                    return made;
                }
            }
            catch
            {
                return font;
            }
        }
    }
}
