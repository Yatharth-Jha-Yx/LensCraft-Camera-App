using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LensCraft.Models;
using LensCraft.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace LensCraft.ViewModels;

/// <summary>
/// State for the main window and all floating controls. Every camera call goes through
/// CameraService; this class only decides what the user sees.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxRecentPhotos = 6; // spec §3: last 6 captures in the strip

#if DEBUG
    // Spec §7 rule 5: the hardware panel is the live debugging surface, so Debug builds open with it showing.
    private const bool OpenHardwarePanelByDefault = true;
#else
    private const bool OpenHardwarePanelByDefault = false;
#endif

    private readonly CameraService _camera;
    private readonly CaptureService _capture;
    private readonly HardwareInfoService _hardware;
    private readonly DispatcherQueue _dispatcher;

    private bool _suppressSelectionHandlers;
    private bool _isRecovering;
    private int _startAttempt;
    private BannerAction _bannerAction;
    private double? _measuredFps;

    public MainViewModel(CameraService camera, CaptureService capture, HardwareInfoService hardware, DispatcherQueue dispatcher)
    {
        _camera = camera;
        _capture = capture;
        _hardware = hardware;
        _dispatcher = dispatcher;

        _camera.CameraFailed += OnCameraFailed;
        _camera.DevicesChanged += OnDevicesChanged;
        _camera.MeasuredFrameRateChanged += OnMeasuredFrameRateChanged;
        RecentPhotos.CollectionChanged += OnRecentPhotosChanged;
    }

    private enum BannerAction
    {
        None,
        Retry,
        OpenSettings,
    }

    /// <summary>The window plays the capture flash when this fires.</summary>
    public event EventHandler? FlashRequested;

    public ObservableCollection<CameraDeviceInfo> Cameras { get; } = new();

    public ObservableCollection<CameraFormat> Formats { get; } = new();

    public ObservableCollection<CapturedPhoto> RecentPhotos { get; } = new();

    // Hardware panel groups (spec §5). Capabilities reuses Formats.
    public ObservableCollection<HardwareInfoRow> IdentityRows { get; } = new();

    public ObservableCollection<HardwareInfoRow> ActiveFormatRows { get; } = new();

    public ObservableCollection<HardwareInfoRow> ControlRows { get; } = new();

    public ObservableCollection<HardwareInfoRow> SessionRows { get; } = new();

    public MediaPlayer PreviewPlayer => _camera.PreviewPlayer;

    [ObservableProperty]
    private CameraDeviceInfo? selectedCamera;

    [ObservableProperty]
    private CameraFormat? selectedFormat;

    /// <summary>Spec §4: preview mirrored ON by default, like a real mirror.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewScaleX))]
    private bool isPreviewMirrored = true;

    /// <summary>Spec §4: saved photos NOT mirrored by default, matching what others see.</summary>
    [ObservableProperty]
    private bool isSaveMirrored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    [NotifyPropertyChangedFor(nameof(IsFormatPickerEnabled))]
    [NotifyCanExecuteChangedFor(nameof(CapturePhotoCommand))]
    private bool isCameraReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormatPickerEnabled))]
    [NotifyCanExecuteChangedFor(nameof(CapturePhotoCommand))]
    private bool isSwitchingFormat;

    [ObservableProperty]
    private string emptyStateText = "Starting camera…";

    [ObservableProperty]
    private bool isHardwarePanelOpen = OpenHardwarePanelByDefault;

    [ObservableProperty]
    private bool isBannerOpen;

    [ObservableProperty]
    private string bannerTitle = string.Empty;

    [ObservableProperty]
    private string bannerMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity bannerSeverity = InfoBarSeverity.Warning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerActionVisible))]
    private string bannerActionText = string.Empty;

    /// <summary>Bound to the preview's ScaleTransform: -1 flips it horizontally (spec §4).</summary>
    public double PreviewScaleX => IsPreviewMirrored ? -1 : 1;

    public bool IsEmptyStateVisible => !IsCameraReady;

    public bool IsFormatPickerEnabled => IsCameraReady && !IsSwitchingFormat;

    public bool IsBannerActionVisible => BannerActionText.Length > 0;

    public bool HasRecentPhotos => RecentPhotos.Count > 0;

    /// <summary>Called once when the window has loaded.</summary>
    public async Task InitializeAsync()
    {
        await SetFrameRateMeterAsync(IsHardwarePanelOpen);
        await LoadRecentPhotosAsync();
        try
        {
            await RefreshCamerasAsync();
            _camera.StartWatchingDevices();
        }
        catch (Exception ex)
        {
            ShowNoCameraState("LensCraft could not list cameras.");
            ShowBanner("Cameras not listed", $"Windows did not return the camera list. Press Retry. ({ex.Message})", InfoBarSeverity.Error, BannerAction.Retry);
            return;
        }

        if (Cameras.Count == 0)
        {
            ShowNoCameraState();
            return;
        }

        SelectedCamera = Cameras[0]; // starts the preview through OnSelectedCameraChanged
    }

    public void Dispose()
    {
        _camera.CameraFailed -= OnCameraFailed;
        _camera.DevicesChanged -= OnDevicesChanged;
        _camera.MeasuredFrameRateChanged -= OnMeasuredFrameRateChanged;
        RecentPhotos.CollectionChanged -= OnRecentPhotosChanged;
        _camera.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanCapturePhoto))]
    private async Task CapturePhotoAsync()
    {
        FlashRequested?.Invoke(this, EventArgs.Empty);

        StorageFile file;
        try
        {
            file = await _capture.CapturePhotoAsync(IsSaveMirrored);
        }
        catch (Exception ex)
        {
            ShowBanner("Photo not saved", $"The camera did not return a photo. Press the shutter again. ({ex.Message})", InfoBarSeverity.Error, BannerAction.None);
            return;
        }

        try
        {
            RecentPhotos.Insert(0, await CapturedPhoto.CreateAsync(file));
            while (RecentPhotos.Count > MaxRecentPhotos)
            {
                RecentPhotos.RemoveAt(RecentPhotos.Count - 1);
            }
        }
        catch (Exception ex)
        {
            // The photo is safely on disk; only its thumbnail failed.
            Debug.WriteLine($"LensCraft: thumbnail failed for {file.Path}: {ex.Message}");
        }
    }

    private bool CanCapturePhoto() => IsCameraReady && !IsSwitchingFormat;

    [RelayCommand]
    private async Task OpenPhotoAsync(CapturedPhoto? photo)
    {
        if (photo is null)
        {
            return;
        }

        bool launched;
        try
        {
            launched = await Launcher.LaunchFileAsync(photo.File);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LensCraft: could not open {photo.File.Path}: {ex.Message}");
            launched = false;
        }

        if (!launched)
        {
            ShowBanner("Photo not opened", $"Windows has no app set to open {photo.File.FileType} files. The photo is in Pictures\\LensCraft.", InfoBarSeverity.Warning, BannerAction.None);
        }
    }

    [RelayCommand]
    private async Task BannerActionAsync()
    {
        switch (_bannerAction)
        {
            case BannerAction.Retry:
                await RetryAsync();
                break;

            case BannerAction.OpenSettings:
                await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
                ShowBanner("Allow camera access", "Turn on camera access for LensCraft in Settings, then press Retry.", InfoBarSeverity.Informational, BannerAction.Retry);
                break;
        }
    }

    /// <summary>Spec §5 copy icon: the whole panel as plain text on the clipboard.</summary>
    [RelayCommand]
    private void CopyHardwareInfo()
    {
        var package = new DataPackage();
        package.SetText(_hardware.BuildReport(_measuredFps));
        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keep the text available after LensCraft closes
            ShowBanner("Camera details copied", "Paste them into a bug report or chat.", InfoBarSeverity.Success, BannerAction.None);
        }
        catch (Exception ex)
        {
            ShowBanner("Camera details not copied", $"Another app is holding the clipboard. Try again. ({ex.Message})", InfoBarSeverity.Warning, BannerAction.None);
        }
    }

    partial void OnIsHardwarePanelOpenChanged(bool value)
    {
        _ = SetFrameRateMeterAsync(value);
        if (value && IsCameraReady)
        {
            RefreshHardwareInfo();
        }
    }

    partial void OnIsCameraReadyChanged(bool value)
    {
        if (value)
        {
            RefreshHardwareInfo();
        }
        else
        {
            // No live camera: the panel shows its "no camera" message instead of stale values.
            _measuredFps = null;
            IdentityRows.Clear();
            ActiveFormatRows.Clear();
            ControlRows.Clear();
            SessionRows.Clear();
        }
    }

    partial void OnSelectedCameraChanged(CameraDeviceInfo? value)
    {
        if (_suppressSelectionHandlers || value is null)
        {
            return;
        }

        _ = StartCameraAsync(value);
    }

    partial void OnSelectedFormatChanged(CameraFormat? value)
    {
        if (_suppressSelectionHandlers || value is null)
        {
            return;
        }

        _ = ApplyFormatAsync(value);
    }

    private async Task StartCameraAsync(CameraDeviceInfo device)
    {
        int attempt = ++_startAttempt;
        IsCameraReady = false;
        EmptyStateText = $"Starting {device.Name}…";
        CloseBanner();

        try
        {
            await _camera.StartAsync(device);
            if (attempt != _startAttempt)
            {
                return; // the user picked another camera while this one was starting
            }

            ReloadFormats();
            IsCameraReady = true;
        }
        catch (UnauthorizedAccessException)
        {
            if (attempt != _startAttempt)
            {
                return;
            }

            EmptyStateText = "Camera access is off for LensCraft.";
            ShowBanner("Camera access is off", "Windows is blocking LensCraft from the camera. Open Settings and allow camera access.", InfoBarSeverity.Error, BannerAction.OpenSettings);
        }
        catch (Exception ex)
        {
            if (attempt != _startAttempt)
            {
                return;
            }

            EmptyStateText = $"{device.Name} is unavailable.";
            ShowBanner("Camera in use", $"Another app may be using {device.Name}. Close that app, then press Retry. ({ex.Message})", InfoBarSeverity.Warning, BannerAction.Retry);
        }
    }

    private async Task ApplyFormatAsync(CameraFormat format)
    {
        if (_camera.ActiveFormat?.Key == format.Key)
        {
            return;
        }

        IsSwitchingFormat = true;
        try
        {
            await _camera.SetFormatAsync(format);
        }
        catch (Exception ex)
        {
            ShowBanner("Format not applied", $"The camera rejected {format}. The previous format is still active. ({ex.Message})", InfoBarSeverity.Warning, BannerAction.None);
        }
        finally
        {
            IsSwitchingFormat = false;
            SyncSelectedFormatToCamera();
            if (IsCameraReady)
            {
                RefreshHardwareInfo(); // spec §5: values refresh when the format changes
            }
        }
    }

    private async Task RetryAsync()
    {
        CloseBanner();
        await RefreshCamerasAsync();
        _camera.StartWatchingDevices(); // no-op when already watching

        CameraDeviceInfo? target = SelectedCamera ?? Cameras.FirstOrDefault();
        if (target is null)
        {
            ShowNoCameraState();
            return;
        }

        SetSelectedCameraSilently(target);
        await StartCameraAsync(target);
    }

    private async Task RefreshCamerasAsync()
    {
        IReadOnlyList<CameraDeviceInfo> found = await _camera.FindCamerasAsync();
        string? selectedId = SelectedCamera?.Id;

        _suppressSelectionHandlers = true;
        try
        {
            Cameras.Clear();
            foreach (CameraDeviceInfo camera in found)
            {
                Cameras.Add(camera);
            }

            SelectedCamera = Cameras.FirstOrDefault(camera => camera.Id == selectedId);
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private void ReloadFormats()
    {
        _suppressSelectionHandlers = true;
        try
        {
            Formats.Clear();
            foreach (CameraFormat format in _camera.GetAvailableFormats())
            {
                Formats.Add(format);
            }
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        SyncSelectedFormatToCamera();
    }

    /// <summary>Highlights the format the camera is actually using, without triggering a switch.</summary>
    private void SyncSelectedFormatToCamera()
    {
        string? activeKey = _camera.ActiveFormat?.Key;
        _suppressSelectionHandlers = true;
        try
        {
            SelectedFormat = activeKey is null ? null : Formats.FirstOrDefault(format => format.Key == activeKey);
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private void SetSelectedCameraSilently(CameraDeviceInfo? camera)
    {
        _suppressSelectionHandlers = true;
        try
        {
            SelectedCamera = camera;
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private void OnCameraFailed(object? sender, CameraFailureEventArgs e)
    {
        RunOnUiThread(async () =>
        {
            // Ignore late failures from a camera we already moved away from.
            if (e.DeviceId is null || e.DeviceId != SelectedCamera?.Id || !IsCameraReady)
            {
                return;
            }

            await HandleCameraLostAsync(e.DeviceId, e.Message);
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(HandleDevicesChangedAsync);
    }

    private async Task HandleDevicesChangedAsync()
    {
        if (_isRecovering)
        {
            return;
        }

        string? activeId = IsCameraReady ? SelectedCamera?.Id : null;
        await RefreshCamerasAsync();

        if (activeId is not null && Cameras.All(camera => camera.Id != activeId))
        {
            await HandleCameraLostAsync(activeId, "The camera was unplugged.");
        }
        else if (!IsCameraReady && SelectedCamera is null && Cameras.Count > 0)
        {
            // Reconnect state: a camera just appeared, so start it.
            CloseBanner();
            SelectedCamera = Cameras[0];
        }
    }

    /// <summary>
    /// Spec §4 error baseline: camera unplugged → switch to the next camera or show a reconnect state;
    /// camera taken by another app → banner with Retry.
    /// </summary>
    private async Task HandleCameraLostAsync(string lostCameraId, string reason)
    {
        if (_isRecovering)
        {
            return;
        }

        _isRecovering = true;
        try
        {
            ++_startAttempt; // cancel any start still in flight
            IsCameraReady = false;
            await _camera.StopAsync();
            await RefreshCamerasAsync();

            if (Cameras.Any(camera => camera.Id == lostCameraId))
            {
                EmptyStateText = "Camera paused.";
                ShowBanner("Camera stopped", $"LensCraft lost the camera feed; another app may have taken it. Close that app, then press Retry. ({reason})", InfoBarSeverity.Warning, BannerAction.Retry);
            }
            else if (Cameras.Count > 0)
            {
                CameraDeviceInfo next = Cameras[0];
                SetSelectedCameraSilently(next);
                await StartCameraAsync(next);
                if (IsCameraReady)
                {
                    ShowBanner("Camera disconnected", $"Switched to {next.Name}.", InfoBarSeverity.Informational, BannerAction.None);
                }
            }
            else
            {
                SetSelectedCameraSilently(null);
                ShowNoCameraState("Camera disconnected. Plug it back in and LensCraft reconnects automatically.");
            }
        }
        finally
        {
            _isRecovering = false;
        }
    }

    private async Task LoadRecentPhotosAsync()
    {
        try
        {
            foreach (StorageFile file in await _capture.GetRecentCapturesAsync(MaxRecentPhotos))
            {
                RecentPhotos.Add(await CapturedPhoto.CreateAsync(file));
            }
        }
        catch (Exception ex)
        {
            // The strip is a convenience; one unreadable file must not block the camera from starting.
            Debug.WriteLine($"LensCraft: could not load recent photos: {ex.Message}");
        }
    }

    private void ShowNoCameraState(string? message = null)
    {
        IsCameraReady = false;
        Formats.Clear();
        EmptyStateText = message ?? "No camera found. Plug one in and LensCraft connects to it automatically.";
    }

    private void ShowBanner(string title, string message, InfoBarSeverity severity, BannerAction action)
    {
        BannerTitle = title;
        BannerMessage = message;
        BannerSeverity = severity;
        _bannerAction = action;
        BannerActionText = action switch
        {
            BannerAction.Retry => "Retry",
            BannerAction.OpenSettings => "Open Settings",
            _ => string.Empty,
        };
        IsBannerOpen = true;
    }

    private void CloseBanner() => IsBannerOpen = false;

    /// <summary>Camera events arrive on background threads; UI state may only change on the UI thread.</summary>
    private void RunOnUiThread(Func<Task> work)
    {
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                ShowBanner("Camera error", $"{ex.Message} Press Retry to reconnect.", InfoBarSeverity.Error, BannerAction.Retry);
            }
        });
    }

    private void RefreshHardwareInfo()
    {
        MergeRows(IdentityRows, _hardware.GetIdentityRows());
        MergeRows(ActiveFormatRows, _hardware.GetActiveFormatRows());
        RefreshLiveHardwareRows();
    }

    /// <summary>Controls and measured FPS change while the camera runs; refreshed once per meter tick.</summary>
    private void RefreshLiveHardwareRows()
    {
        MergeRows(ControlRows, _hardware.GetControlRows());
        MergeRows(SessionRows, _hardware.GetSessionRows(_measuredFps));
    }

    /// <summary>Updates rows in place when the labels match, so the list does not flicker every second.</summary>
    private static void MergeRows(ObservableCollection<HardwareInfoRow> target, IReadOnlyList<HardwareInfoRow> fresh)
    {
        bool sameShape = target.Count == fresh.Count
            && target.Zip(fresh).All(pair => pair.First.Label == pair.Second.Label);

        if (sameShape)
        {
            for (int i = 0; i < fresh.Count; i++)
            {
                target[i].Text = fresh[i].Text;
                target[i].Detail = fresh[i].Detail;
            }

            return;
        }

        target.Clear();
        foreach (HardwareInfoRow row in fresh)
        {
            target.Add(row);
        }
    }

    private async Task SetFrameRateMeterAsync(bool enabled)
    {
        try
        {
            await _camera.SetFrameRateMeterEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LensCraft: could not switch the FPS meter: {ex.Message}");
        }
    }

    private void OnMeasuredFrameRateChanged(object? sender, double? fps)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _measuredFps = fps;
            if (IsCameraReady && IsHardwarePanelOpen)
            {
                RefreshLiveHardwareRows();
            }
        });
    }

    private void OnRecentPhotosChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasRecentPhotos));
}
