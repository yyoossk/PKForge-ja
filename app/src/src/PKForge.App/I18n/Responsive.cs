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

    /// <summary>
    /// アプリ全体の Resources の密度も上げる（MAUI は最初に見た密度を覚えておくので、MAUI の初期化前に呼ぶ）。
    /// </summary>
    public static void ScaleApp(Android.Content.Context app)
    {
        try
        {
            var res = app.Resources;
            var cfg = res?.Configuration;
            var metrics = res?.DisplayMetrics;
            if (res is null || cfg is null || metrics is null) return;
            var scale = ScaleFor(cfg.SmallestScreenWidthDp);
            if (scale <= 1.01f) return;
            var dpi = (int)(cfg.DensityDpi * scale);
            var updated = new Android.Content.Res.Configuration(cfg) { DensityDpi = dpi };
#pragma warning disable CS0618, CA1422
            metrics.DensityDpi = (Android.Util.DisplayMetricsDensity)dpi;
            metrics.Density = dpi / 160f;
            metrics.ScaledDensity = metrics.Density * cfg.FontScale;
            res.UpdateConfiguration(updated, metrics);
#pragma warning restore CS0618, CA1422
        }
        catch
        {
            // 拡大できなくても本家の寸法で表示する
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

    private sealed class SplitState
    {
        public View Main = null!;
        public View Side = null!;
        public double SideWidth;
        public bool? Narrow;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Grid, SplitState> Splits = new();

    /// <summary>
    /// 「左に本体・右に固定幅の詳細パネル」の 2 列レイアウト。幅が狭い（縦持ちのスマホなど）ときは
    /// 本体を上、詳細を下に積む。
    /// </summary>
    public static void SidePanel(Grid grid, View main, View side, double sideWidth)
    {
        var state = new SplitState { Main = main, Side = side, SideWidth = sideWidth };
        Splits.AddOrUpdate(grid, state);
        grid.SizeChanged += (_, _) =>
        {
            if (grid.Width <= 0 || grid.Height <= 0) return;
            var narrow = grid.Width < sideWidth * 2.1 || grid.Height > grid.Width * 1.05;
            if (narrow == state.Narrow) return;
            state.Narrow = narrow;
            Apply(grid, state);
        };
        side.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(View.IsVisible) && state.Narrow is not null) Apply(grid, state);
        };
    }

    /// <summary>詳細パネルを元に戻すとき（本家は列幅を固定値に戻すので、その代わりに呼ぶ）</summary>
    public static void RestoreSide(Grid grid)
    {
        if (Splits.TryGetValue(grid, out var state) && state.Narrow is not null)
        {
            Apply(grid, state);
            return;
        }
        if (grid.ColumnDefinitions.Count > 1) grid.ColumnDefinitions[1].Width = new GridLength(state?.SideWidth ?? 330);
        grid.ColumnSpacing = 12;
    }

    private static void Apply(Grid grid, SplitState state)
    {
        var visible = state.Side.IsVisible;
        if (state.Narrow == true)
        {
            if (grid.ColumnDefinitions.Count > 1) grid.ColumnDefinitions[1].Width = new GridLength(0);
            grid.ColumnSpacing = 0;
            grid.RowDefinitions.Clear();
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1.25, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0)));
            grid.RowSpacing = visible ? 10 : 0;
            Grid.SetRow(state.Main, 0);
            Grid.SetColumn(state.Main, 0);
            Grid.SetRow(state.Side, 1);
            Grid.SetColumn(state.Side, 0);
        }
        else
        {
            grid.RowDefinitions.Clear();
            grid.RowSpacing = 0;
            if (grid.ColumnDefinitions.Count > 1) grid.ColumnDefinitions[1].Width = visible ? new GridLength(state.SideWidth) : new GridLength(0);
            grid.ColumnSpacing = visible ? 12 : 0;
            Grid.SetRow(state.Main, 0);
            Grid.SetColumn(state.Main, 0);
            Grid.SetRow(state.Side, 0);
            Grid.SetColumn(state.Side, 1);
        }
    }
}
