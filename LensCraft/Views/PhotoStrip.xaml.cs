using LensCraft.Models;
using LensCraft.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace LensCraft.Views;

/// <summary>Bottom-edge strip of the newest captures.</summary>
public sealed partial class PhotoStrip : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(PhotoStrip), new PropertyMetadata(null));

    public PhotoStrip()
    {
        InitializeComponent();
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OnStripPointerEntered(object sender, PointerRoutedEventArgs e) => SlideUpStoryboard.Begin();

    private void OnStripPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // PointerExited also bubbles up from each thumbnail; only slide down once the pointer has left the strip.
        Point position = e.GetCurrentPoint(StripRoot).Position;
        bool stillInside = position.X >= 0 && position.Y >= 0
            && position.X < StripRoot.ActualWidth && position.Y < StripRoot.ActualHeight;
        if (!stillInside)
        {
            SlideDownStoryboard.Begin();
        }
    }

    private void OnThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CapturedPhoto photo })
        {
            ViewModel?.OpenPhotoCommand.Execute(photo);
        }
    }
}
