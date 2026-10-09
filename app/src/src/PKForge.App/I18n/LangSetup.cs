// PKForge Dex（多言語版）: 表示言語の設定。PKForge-ja リポジトリの app/src から本家ソースへコピーされる。
#nullable enable
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using PKForge.App.Views;

namespace PKForgeI18n;

public static class LangSetup
{
    private const string PreferenceKey = "pkf_i18n_lang";

    /// <summary>番号は pkforge_i18n.py の LANGS と同じ並び。</summary>
    private static readonly (string Code, string PkHex, string Native)[] Languages =
    [
        ("en", "en", "English"),
        ("ja", "ja", "日本語"),
        ("zh-Hans", "zh-Hans", "简体中文"),
        ("zh-Hant", "zh-Hant", "繁體中文"),
        ("ko", "ko", "한국어"),
        ("fr", "fr", "Français"),
        ("de", "de", "Deutsch"),
        ("es", "es", "Español"),
        ("it", "it", "Italiano"),
    ];

    // メニュー用の短い文言（各言語）
    private static readonly string[] MenuLabel =
        ["Language", "言語 / Language", "语言 / Language", "語言 / Language", "언어 / Language", "Langue / Language", "Sprache / Language", "Idioma / Language", "Lingua / Language"];
    private static readonly string[] AutoLabel =
        ["Device language", "端末の言語に合わせる", "跟随系统语言", "跟隨系統語言", "기기 언어 따르기", "Langue de l'appareil", "Gerätesprache", "Idioma del dispositivo", "Lingua del dispositivo"];
    private static readonly string[] RestartTitle =
        ["Restart to apply", "再起動して反映", "重启以应用", "重新啟動以套用", "다시 시작하여 적용", "Redémarrer pour appliquer", "Neustart zum Übernehmen", "Reiniciar para aplicar", "Riavvia per applicare"];
    private static readonly string[] RestartBody =
        ["PKForge will restart now in the new language.", "新しい言語で PKForge を再起動します。", "PKForge 将以新语言重新启动。", "PKForge 將以新語言重新啟動。", "새 언어로 PKForge를 다시 시작합니다.", "PKForge va redémarrer dans la nouvelle langue.", "PKForge startet jetzt in der neuen Sprache neu.", "PKForge se reiniciará con el nuevo idioma.", "PKForge verrà riavviato nella nuova lingua."];
    private static readonly string[] RestartButton =
        ["Restart", "再起動", "重启", "重新啟動", "다시 시작", "Redémarrer", "Neu starten", "Reiniciar", "Riavvia"];

    /// <summary>現在の言語番号。</summary>
    public static int Current { get; private set; }

    /// <summary>設定メニューに出す項目名（現在の言語で）。</summary>
    public static string MenuText => MenuLabel[Current];

    /// <summary>起動直後（MauiProgram.CreateMauiApp の先頭）で呼ぶ。</summary>
    public static void Init()
    {
        try
        {
            var saved = Preferences.Default.Get(PreferenceKey, "auto");
            var index = IndexOf(saved);
            if (index < 0)
                index = L_App.FromCulture(DeviceLanguageTag());
            Current = index;
            System.AppContext.SetData("PKForgeI18n.Lang", index);
            var code = Languages[index].PkHex;
            PKHeX.Core.GameInfo.CurrentLanguage = code;
            PKHeX.Core.GameInfo.Strings = PKHeX.Core.GameInfo.GetStrings(code);
        }
        catch
        {
            Current = 0;
            System.AppContext.SetData("PKForgeI18n.Lang", 0);
        }
    }

    /// <summary>端末（またはアプリ別の言語設定）の言語タグ。.NET の CultureInfo は Android の設定を拾わないことがあるので直接読む。</summary>
    private static string DeviceLanguageTag()
    {
#if ANDROID
        try
        {
            var locales = Android.App.Application.Context.Resources?.Configuration?.Locales;
            if (locales is not null && locales.Size() > 0)
            {
                var tag = locales.Get(0)?.ToLanguageTag();
                if (!string.IsNullOrEmpty(tag)) return tag;
            }
            var def = Java.Util.Locale.Default?.ToLanguageTag();
            if (!string.IsNullOrEmpty(def)) return def;
        }
        catch
        {
        }
#endif
        return System.Globalization.CultureInfo.CurrentUICulture.Name;
    }

    private static int IndexOf(string code)
    {
        for (var i = 0; i < Languages.Length; i++)
            if (string.Equals(Languages[i].Code, code, System.StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>言語を選ぶメニュー。選んだら保存して再起動する。</summary>
    public static async System.Threading.Tasks.Task ShowPickerAsync(Grid host)
    {
        var saved = Preferences.Default.Get(PreferenceKey, "auto");
        var options = new System.Collections.Generic.List<PadOption>
        {
            new((saved == "auto" ? "✓ " : "   ") + AutoLabel[Current], IconPath: "gears"),
        };
        foreach (var lang in Languages)
            options.Add(new PadOption((saved == lang.Code ? "✓ " : "   ") + lang.Native));
        var choice = await PadMenu.ShowAsync(host, MenuLabel[Current], null, [.. options]);
        if (choice is null) return;
        var picked = choice.Trim().TrimStart('✓').Trim();
        string code;
        if (picked == AutoLabel[Current]) code = "auto";
        else
        {
            code = "";
            foreach (var lang in Languages)
                if (lang.Native == picked) code = lang.Code;
            if (code.Length == 0) return;
        }
        if (code == saved) return;
        Preferences.Default.Set(PreferenceKey, code);
        var next = code == "auto" ? L_App.FromCulture(DeviceLanguageTag()) : IndexOf(code);
        if (next == Current) return;
        await PadMenu.ShowAsync(host, RestartTitle[next], RestartBody[next], RestartButton[next]);
        Restart();
    }

    private static void Restart()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
            if (intent is not null)
            {
                intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.ClearTask);
                context.StartActivity(intent);
            }
        }
        catch
        {
            // 失敗しても終了はする（次に開いたときに新しい言語になる）
        }
        Java.Lang.JavaSystem.Exit(0);
#else
        Application.Current?.Quit();
#endif
    }
}
