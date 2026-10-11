// PKForge Dex: 画面サイズ・向きへの対応（縦長スマホ・タブレット・横長携帯機）
#nullable enable
using Microsoft.Maui.Controls;

namespace PKForgeI18n;

public static class Responsive
{
    /// <summary>
    /// タブレットなど大きい画面では、本家の寸法（6 インチ前後の横長携帯機向け）のままだと
    /// 文字もボタンも小さすぎるので、画面全体を拡大する倍率（1 = そのまま）。
    /// 最小幅が 520dp 相当になるように拡大し、上限は 1.6 倍。
    /// </summary>
    public static float ScaleFor(int smallestWidthDp)
    {
        if (smallestWidthDp < 600) return 1f;
        return System.Math.Clamp(smallestWidthDp / 520f, 1f, 1.6f);
    }

    /// <summary>
    /// ホームの 5 枚のカード。幅が狭い（縦持ちのスマホ）ときは 3 列 × 2 段にして、
    /// 1 枚ずつを大きく・押しやすくする。
    /// </summary>
    public static void Cards(Grid grid)
    {
        var lastNarrow = (bool?)null;
        grid.SizeChanged += (_, _) =>
        {
            if (grid.Width <= 0) return;
            var narrow = grid.Width < 520;
            if (narrow == lastNarrow) return;
            lastNarrow = narrow;
            var items = new System.Collections.Generic.List<IView>(grid.Children);
            var columns = narrow ? 3 : System.Math.Max(1, items.Count);
            grid.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var rows = (items.Count + columns - 1) / columns;
            grid.RowDefinitions.Clear();
            for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowSpacing = 10;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is not BindableObject view) continue;
                Grid.SetColumn(view, i % columns);
                Grid.SetRow(view, i / columns);
            }
        };
    }

#if ANDROID
    /// <summary>大きい画面なら密度を上げた Context を返す（Activity と Application の両方で使う）</summary>
    public static Android.Content.Context? Scaled(Android.Content.Context? context)
    {
        try
        {
            var cfg = context?.Resources?.Configuration;
            if (cfg is null) return context;
            var scale = ScaleFor(cfg.SmallestScreenWidthDp);
            if (scale <= 1.01f) return context;
            // 密度だけを上書き（向きが変わっても倍率が保たれるよう、幅・高さは上書きしない）
            var over = new Android.Content.Res.Configuration { DensityDpi = (int)(cfg.DensityDpi * scale) };
            return context!.CreateConfigurationContext(over) ?? context;
        }
        catch
        {
            return context;
        }
    }
#endif

    /// <summary>メニューの列数を、画面の実際の幅と文字の幅（全角は 2 文字分）から決める。</summary>
    public static int MenuColumns(System.Collections.Generic.IEnumerable<string> labels, int count, double hostWidth)
    {
        var longest = 0;
        foreach (var label in labels)
        {
            var w = 0;
            foreach (var ch in label) w += ch >= 0x1100 ? 2 : 1;
            if (w > longest) longest = w;
        }
        var width = System.Math.Min(hostWidth > 0 ? hostWidth - 40 : 600, 640);
        var max = count <= 4 ? 1 : count <= 12 ? 2 : 3;
        for (var c = max; c > 1; c--)
        {
            var column = (width - (c - 1) * 8) / c - 52;   // アイコンと余白
            if (longest * 8.2 <= column) return c;
        }
        return 1;
    }

    /// <summary>ボタンが横に並ぶ帯。幅が足りないときは横スクロールできるようにする。</summary>
    public static View ScrollIfNarrow(View strip)
    {
        var scroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = strip,
        };
        scroll.SizeChanged += (_, _) =>
        {
            if (scroll.Width > 0) strip.MinimumWidthRequest = scroll.Width;
        };
        return scroll;
    }
}
