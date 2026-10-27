using Microsoft.UI.Xaml.Controls;

namespace LensCraft.Views;

/// <summary>The capture flash. Never takes clicks, so it can sit above everything.</summary>
public sealed partial class FlashOverlay : UserControl
{
    public FlashOverlay()
    {
        InitializeComponent();
    }

    public void Flash()
    {
        FlashStoryboard.Stop();
        FlashStoryboard.Begin();
    }
}
