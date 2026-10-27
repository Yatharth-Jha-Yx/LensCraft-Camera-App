using LensCraft.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LensCraft.Views;

/// <summary>Top-right pill: hardware panel toggle, plus the settings flyout (camera, resolution/FPS, save mirroring).</summary>
public sealed partial class TopControlCluster : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(TopControlCluster), new PropertyMetadata(null));

    public TopControlCluster()
    {
        InitializeComponent();
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
