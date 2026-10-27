using LensCraft.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LensCraft.Views;

/// <summary>
/// Spec §5: 320 px right-edge panel with everything Windows reports about the active camera.
/// Slides in and out when IsOpen changes.
/// </summary>
public sealed partial class HardwarePanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(HardwarePanel), new PropertyMetadata(null));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(HardwarePanel), new PropertyMetadata(false, OnIsOpenChanged));

    public HardwarePanel()
    {
        InitializeComponent();
        SlideOutStoryboard.Completed += OnSlideOutCompleted;
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HardwarePanel)d).Slide((bool)e.NewValue);

    private void Slide(bool open)
    {
        if (open)
        {
            PanelRoot.Visibility = Visibility.Visible;
            SlideInStoryboard.Begin();
        }
        else
        {
            SlideOutStoryboard.Begin();
        }
    }

    private void OnSlideOutCompleted(object? sender, object e)
    {
        // Collapse once off-screen so the hidden panel never catches clicks.
        if (!IsOpen)
        {
            PanelRoot.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.IsHardwarePanelOpen = false;
        }
    }
}
