using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace PKForgeJa
{
    /// <summary>Label.Text などにバインドされた文字列を表示時に訳すコンバーター。
    /// 元のコンバーターがあれば、その結果を訳す。</summary>
    public sealed class JaConverter : IValueConverter
    {
        private readonly IValueConverter? _inner;

        public JaConverter(IValueConverter? inner) { _inner = inner; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var v = _inner != null ? _inner.Convert(value, targetType, parameter, culture) : value;
            return v is string s ? JaText.T(s) : v;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => _inner != null ? _inner.ConvertBack(value, targetType, parameter, culture) : value;
    }

    public static class JaBinding
    {
        /// <summary>BindableObject.SetBinding(property, binding) の置き換え。</summary>
        public static void SetBinding(BindableObject target, BindableProperty property, BindingBase binding)
        {
            Wrap(property, binding);
            target.SetBinding(property, binding);
        }

        /// <summary>拡張メソッド SetBinding(self, property, path, mode, converter, stringFormat) の置き換え。</summary>
        public static void SetBindingPath(BindableObject self, BindableProperty targetProperty, string path,
            BindingMode mode, IValueConverter? converter, string? stringFormat)
        {
            var binding = new Binding(path, mode, converter, null, stringFormat);
            Wrap(targetProperty, binding);
            self.SetBinding(targetProperty, binding);
        }

        /// <summary>文字を表示するプロパティへのバインディングなら、訳すコンバーターを挟む。</summary>
        public static void Wrap(BindableProperty property, BindingBase binding)
        {
            try
            {
                if (!IsTextProperty(property)) return;
                if (binding is Binding b && b.Converter is not JaConverter && string.IsNullOrEmpty(b.StringFormat))
                    b.Converter = new JaConverter(b.Converter);
            }
            catch
            {
                // 失敗しても英語で表示されるだけ
            }
        }

        private static bool IsTextProperty(BindableProperty p) =>
            ReferenceEquals(p, Label.TextProperty) || ReferenceEquals(p, Button.TextProperty)
            || ReferenceEquals(p, Span.TextProperty) || ReferenceEquals(p, Page.TitleProperty)
            || ReferenceEquals(p, InputView.PlaceholderProperty);
    }
}
