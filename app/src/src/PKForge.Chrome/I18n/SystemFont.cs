// PKForge Dex（多言語版）: キャンバスに描く文字も端末のシステムフォントで描く。
// 日本語・中国語・韓国語では、その言語の文字を持つシステムフォント（Noto Sans CJK など）を選ぶ。
#nullable enable
using SkiaSharp;

namespace PKForgeI18n
{
    public static class SystemFont
    {
        private static SKTypeface? _face;
        private static SKTypeface? _bold;
        private static int _lang = -1;

        public static SKTypeface Face { get { Ensure(); return _face!; } }
        public static SKTypeface Bold { get { Ensure(); return _bold!; } }

        private static void Ensure()
        {
            var lang = DisplayLanguage();
            if (_face is not null && lang == _lang) return;
            SKTypeface? face = null;
            try
            {
                // 1 ja, 2 zh-Hans, 3 zh-Hant, 4 ko（pkforge_i18n.py の LANGS と同じ並び）
                var probe = lang switch { 1 => 'あ', 2 => '汉', 3 => '漢', 4 => '가', _ => '\0' };
                if (probe != '\0') face = SKFontManager.Default.MatchCharacter(probe);
                face ??= SKFontManager.Default.MatchFamily("sans-serif") ?? SKTypeface.Default;
            }
            catch
            {
                face = SKTypeface.Default;
            }
            SKTypeface? bold = null;
            try { bold = SKFontManager.Default.MatchFamily(face.FamilyName, SKFontStyle.Bold); } catch { }
            _face = face;
            _bold = bold ?? face;
            _lang = lang;
        }

        private static int DisplayLanguage() =>
            System.AppContext.GetData("PKForgeI18n.Lang") is int i ? i : 0;
    }
}
