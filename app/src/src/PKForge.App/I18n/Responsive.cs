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
}
