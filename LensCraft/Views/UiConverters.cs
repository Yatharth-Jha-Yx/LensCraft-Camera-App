using System.Globalization;
using Microsoft.UI.Xaml;

namespace LensCraft.Views;

/// <summary>Small functions used directly in x:Bind expressions.</summary>
public static class UiConverters
{
    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatCount(int count)
        => count == 1 ? "1 supported format" : string.Create(CultureInfo.InvariantCulture, $"{count} supported formats");
}
