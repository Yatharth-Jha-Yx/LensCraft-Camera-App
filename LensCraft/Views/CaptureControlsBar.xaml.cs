using LensCraft.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LensCraft.Views;

/// <summary>Bottom-center pill: shutter and mirror toggle. Works on any page given a MainViewModel.</summary>
public sealed partial class CaptureControlsBar : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(CaptureControlsBar), new PropertyMetadata(null));

    public CaptureControlsBar()
    {
        InitializeComponent();
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
