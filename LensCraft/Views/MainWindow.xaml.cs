using System.ComponentModel;
using LensCraft.Services;
using LensCraft.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace LensCraft.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        var camera = new CameraService();
        ViewModel = new MainViewModel(camera, new CaptureService(camera), new HardwareInfoService(camera), DispatcherQueue);

        InitializeComponent();

        // Full-bleed preview: content runs under the title bar; AppTitleBar stays draggable.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }

        AppWindow.Resize(new SizeInt32(1280, 800));

        // The one place the view meets the preview pipeline: hand the player to the element.
        PreviewElement.SetMediaPlayer(ViewModel.PreviewPlayer);

        ViewModel.FlashRequested += OnFlashRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RootGrid.Loaded += OnRootGridLoaded;
        Closed += OnClosed;
    }

    public MainViewModel ViewModel { get; }

    private async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        await ViewModel.InitializeAsync();
    }

    private void OnFlashRequested(object? sender, System.EventArgs e) => FlashLayer.Flash();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCameraReady))
        {
            UpdateCaptionButtonColors();
        }
    }

    /// <summary>
    /// Minimize/maximize/close sit on top of the camera feed while it is live, so they turn white
    /// (like any video player). With no camera, they go back to the theme default over Mica.
    /// </summary>
    private void UpdateCaptionButtonColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        if (ViewModel.IsCameraReady)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.FlashRequested -= OnFlashRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose(); // releases the camera so its privacy light turns off
    }
}
