using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LensCraft.Models;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace LensCraft.Services;

/// <summary>
/// The only class in LensCraft that touches MediaCapture (spec §2). It owns the camera session,
/// the preview player, format switching, photo capture, and plug/unplug detection, so a device
/// change or an error is handled in one place.
/// </summary>
public sealed class CameraService : IDisposable
{
    // One camera operation at a time: start, format switch, capture, stop.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MediaCapture? _mediaCapture;
    private MediaFrameSource? _previewFrameSource;
    private MediaSource? _previewSource;
    private DeviceWatcher? _deviceWatcher;
    private volatile bool _initialEnumerationDone;
    private bool _disposed;

    // Live FPS meter (spec §5 Session group). Runs only while the hardware panel is open.
    private readonly Stopwatch _meterClock = new();
    private MediaFrameReader? _frameReader;
    private Timer? _meterTimer;
    private bool _meterEnabled;
    private long _frameCount;

    public CameraService()
    {
        // One player for the app's lifetime; only its Source changes when the camera or format changes.
        PreviewPlayer = new MediaPlayer
        {
            AutoPlay = true,
            RealTimePlayback = true, // live camera feed: skip buffering for the lowest latency
            IsMuted = true,
        };

        // Keep the camera preview out of Windows' media flyout and away from the keyboard media keys.
        PreviewPlayer.CommandManager.IsEnabled = false;
        PreviewPlayer.MediaFailed += OnPreviewFailed;
    }

    /// <summary>Raised on a background thread when the running camera stops delivering frames.</summary>
    public event EventHandler<CameraFailureEventArgs>? CameraFailed;

    /// <summary>Raised on a background thread when a camera is plugged in or removed.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>
    /// Raised on a background thread once per second with the measured preview FPS,
    /// or with null when the meter stops or this camera cannot share frames with it.
    /// </summary>
    public event EventHandler<double?>? MeasuredFrameRateChanged;

    /// <summary>The view hands this to MediaPlayerElement.SetMediaPlayer once at startup.</summary>
    public MediaPlayer PreviewPlayer { get; }

    public CameraDeviceInfo? ActiveDevice { get; private set; }

    public CameraFormat? ActiveFormat { get; private set; }

    /// <summary>Spec §4: DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).</summary>
    public async Task<IReadOnlyList<CameraDeviceInfo>> FindCamerasAsync()
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(CameraDeviceInfo.FromDeviceInformation).ToList();
    }

    /// <summary>
    /// Opens the camera and starts the live preview. Throws UnauthorizedAccessException when Windows
    /// privacy settings block camera access; other exceptions usually mean another app holds the camera.
    /// </summary>
    public async Task StartAsync(CameraDeviceInfo device)
    {
        await _gate.WaitAsync();
        try
        {
            StopCore();

            var capture = new MediaCapture();
            capture.Failed += OnMediaCaptureFailed; // spec §4: MediaCapture.Failed is always subscribed
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = device.Id,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    // Exclusive control is what allows LensCraft to change resolution and frame rate.
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                    MemoryPreference = MediaCaptureMemoryPreference.Auto,
                });

                _mediaCapture = capture;
                _previewFrameSource = PickPreviewFrameSource(capture);
                ActiveDevice = device;
                ActiveFormat = ReadActiveFormat();
                AttachPreview();
                await StartMeterIfEnabledAsync();
            }
            catch
            {
                _mediaCapture = capture; // let StopCore unsubscribe and release it
                StopCore();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Spec §4: every format the hardware reports for the preview stream, largest first.
    /// The driver can also report photo formats here, so only VideoEncodingProperties are kept.
    /// </summary>
    public IReadOnlyList<CameraFormat> GetAvailableFormats()
    {
        if (_mediaCapture is null || _previewFrameSource is null)
        {
            return Array.Empty<CameraFormat>();
        }

        return _mediaCapture.VideoDeviceController
            .GetAvailableMediaStreamProperties(_previewFrameSource.Info.MediaStreamType)
            .OfType<VideoEncodingProperties>()
            .Select(properties => new CameraFormat(properties))
            .Where(format => format.Width > 0 && format.Height > 0 && format.FrameRate > 0)
            .GroupBy(format => format.Key) // some drivers list the same format twice
            .Select(group => group.First())
            .OrderByDescending(format => (long)format.Width * format.Height)
            .ThenByDescending(format => format.FrameRate)
            .ThenBy(format => format.Subtype, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Spec §4: VideoDeviceController.SetMediaStreamPropertiesAsync. The preview is detached first
    /// because the camera rejects format changes while frames are streaming to a MediaPlayerElement.
    /// </summary>
    public async Task SetFormatAsync(CameraFormat format)
    {
        await _gate.WaitAsync();
        try
        {
            if (_mediaCapture is null || _previewFrameSource is null)
            {
                throw new InvalidOperationException("No camera is running.");
            }

            // Both preview consumers must stop streaming before the camera accepts a new format.
            await StopMeterAsync();
            DetachPreview();
            try
            {
                await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                    _previewFrameSource.Info.MediaStreamType, format.Properties);

                // The preview reads from the frame source. If it did not pick up the new format,
                // apply the same format through the frame source's own documented API.
                if (!Matches(_previewFrameSource.CurrentFormat, format))
                {
                    MediaFrameFormat? match = _previewFrameSource.SupportedFormats.FirstOrDefault(f => Matches(f, format));
                    if (match is not null)
                    {
                        await _previewFrameSource.SetFormatAsync(match);
                    }
                }
            }
            finally
            {
                // Always bring the preview back, even when the driver rejected the format.
                ActiveFormat = ReadActiveFormat();
                AttachPreview();
                await StartMeterIfEnabledAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Spec §5 Controls group: exposure, white balance, brightness, contrast through
    /// VideoDeviceController. A control whose Capabilities.Supported is false is left out.
    /// </summary>
    public IReadOnlyList<CameraControlReading> GetControlReadings()
    {
        if (_mediaCapture is null)
        {
            return [];
        }

        VideoDeviceController controller = _mediaCapture.VideoDeviceController;
        var readings = new List<CameraControlReading>();
        AddReading(readings, "Exposure", controller.Exposure);
        AddReading(readings, "White balance", controller.WhiteBalance);
        AddReading(readings, "Brightness", controller.Brightness);
        AddReading(readings, "Contrast", controller.Contrast);
        return readings;
    }

    /// <summary>Turns the live FPS meter on or off. Off saves CPU while the hardware panel is closed.</summary>
    public async Task SetFrameRateMeterEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            _meterEnabled = enabled;
            if (enabled)
            {
                await StartMeterIfEnabledAsync();
            }
            else
            {
                await StopMeterAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Spec §4: MediaCapture.CapturePhotoToStorageFileAsync, JPEG.</summary>
    public async Task CapturePhotoToStorageFileAsync(StorageFile file)
    {
        await _gate.WaitAsync();
        try
        {
            if (_mediaCapture is null)
            {
                throw new InvalidOperationException("No camera is running.");
            }

            await _mediaCapture.CapturePhotoToStorageFileAsync(ImageEncodingProperties.CreateJpeg(), file);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the camera (the privacy light turns off).</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts watching for cameras being plugged in or removed.</summary>
    public void StartWatchingDevices()
    {
        if (_deviceWatcher is not null)
        {
            return;
        }

        _deviceWatcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        _deviceWatcher.Added += (_, _) => RaiseDevicesChanged();
        _deviceWatcher.Removed += (_, _) => RaiseDevicesChanged();
        // Windows only delivers Added/Removed reliably when Updated is also handled.
        _deviceWatcher.Updated += (_, _) => { };
        _deviceWatcher.EnumerationCompleted += (_, _) => _initialEnumerationDone = true;
        _deviceWatcher.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_deviceWatcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
        {
            _deviceWatcher.Stop();
        }

        StopCore();
        PreviewPlayer.MediaFailed -= OnPreviewFailed;
        PreviewPlayer.Dispose();
    }

    /// <summary>
    /// Prefer the camera's dedicated preview stream; many webcams only expose a record stream,
    /// which then doubles as the preview.
    /// </summary>
    private static MediaFrameSource PickPreviewFrameSource(MediaCapture capture)
    {
        List<MediaFrameSource> colorSources = capture.FrameSources.Values
            .Where(source => source.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();

        return colorSources.FirstOrDefault(source => source.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? colorSources.FirstOrDefault(source => source.Info.MediaStreamType == MediaStreamType.VideoRecord)
            ?? colorSources.FirstOrDefault()
            ?? throw new InvalidOperationException("This camera does not provide a color video stream.");
    }

    private static bool Matches(MediaFrameFormat? frameFormat, CameraFormat format)
    {
        if (frameFormat?.VideoFormat is null || frameFormat.FrameRate is null || frameFormat.FrameRate.Denominator == 0)
        {
            return false;
        }

        double fps = (double)frameFormat.FrameRate.Numerator / frameFormat.FrameRate.Denominator;
        return frameFormat.VideoFormat.Width == format.Width
            && frameFormat.VideoFormat.Height == format.Height
            && Math.Abs(fps - format.FrameRate) < 0.5
            && string.Equals(frameFormat.Subtype, format.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    private CameraFormat? ReadActiveFormat()
    {
        if (_mediaCapture is null || _previewFrameSource is null)
        {
            return null;
        }

        return _mediaCapture.VideoDeviceController.GetMediaStreamProperties(_previewFrameSource.Info.MediaStreamType)
            is VideoEncodingProperties properties
            ? new CameraFormat(properties)
            : null;
    }

    /// <summary>WinUI 3 has no CaptureElement: the documented route is MediaFrameSource → MediaSource → MediaPlayer.</summary>
    private void AttachPreview()
    {
        if (_previewFrameSource is null)
        {
            return;
        }

        _previewSource = MediaSource.CreateFromMediaFrameSource(_previewFrameSource);
        PreviewPlayer.Source = _previewSource;
        PreviewPlayer.Play();
    }

    private void DetachPreview()
    {
        PreviewPlayer.Pause();
        PreviewPlayer.Source = null;
        _previewSource?.Dispose();
        _previewSource = null;
    }

    private void StopCore()
    {
        DisposeMeter();
        DetachPreview();

        if (_mediaCapture is not null)
        {
            _mediaCapture.Failed -= OnMediaCaptureFailed;
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }

        _previewFrameSource = null;
        ActiveDevice = null;
        ActiveFormat = null;
    }

    private static void AddReading(List<CameraControlReading> readings, string name, MediaDeviceControl control)
    {
        MediaDeviceControlCapabilities capabilities = control.Capabilities;
        if (!capabilities.Supported)
        {
            return; // spec §5: unsupported fields are omitted, not shown as N/A
        }

        double? value = control.TryGetValue(out double current) ? current : null;
        bool? isAuto = capabilities.AutoModeSupported && control.TryGetAuto(out bool auto) ? auto : null;
        readings.Add(new CameraControlReading(name, value, isAuto, capabilities.Min, capabilities.Max, capabilities.Step));
    }

    /// <summary>
    /// Spec §5: a MediaFrameReader on the preview frame source counts frames as they arrive.
    /// A failure here only hides the FPS row; it never stops the camera.
    /// </summary>
    private async Task StartMeterIfEnabledAsync()
    {
        if (!_meterEnabled || _frameReader is not null || _mediaCapture is null || _previewFrameSource is null)
        {
            return;
        }

        try
        {
            MediaFrameReader reader = await _mediaCapture.CreateFrameReaderAsync(_previewFrameSource);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnMeterFrameArrived;

            MediaFrameReaderStartStatus status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnMeterFrameArrived;
                reader.Dispose();
                Debug.WriteLine($"LensCraft: FPS meter unavailable ({status}).");
                MeasuredFrameRateChanged?.Invoke(this, null);
                return;
            }

            _frameReader = reader;
            Interlocked.Exchange(ref _frameCount, 0);
            _meterClock.Restart();
            _meterTimer = new Timer(OnMeterTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LensCraft: FPS meter failed to start: {ex.Message}");
            DisposeMeter();
        }
    }

    private async Task StopMeterAsync()
    {
        MediaFrameReader? reader = DetachMeter();
        if (reader is null)
        {
            return;
        }

        try
        {
            await reader.StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LensCraft: FPS meter stop failed: {ex.Message}");
        }

        reader.Dispose();
    }

    /// <summary>Synchronous release, used when the whole camera session is torn down.</summary>
    private void DisposeMeter() => DetachMeter()?.Dispose();

    private MediaFrameReader? DetachMeter()
    {
        _meterTimer?.Dispose();
        _meterTimer = null;

        MediaFrameReader? reader = _frameReader;
        _frameReader = null;
        if (reader is not null)
        {
            reader.FrameArrived -= OnMeterFrameArrived;
            MeasuredFrameRateChanged?.Invoke(this, null);
        }

        return reader;
    }

    private void OnMeterFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Acquire and release immediately: the meter only counts frames, it never processes pixels.
        using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
        if (frame is not null)
        {
            Interlocked.Increment(ref _frameCount);
        }
    }

    private void OnMeterTick(object? state)
    {
        if (_frameReader is null)
        {
            return;
        }

        long frames = Interlocked.Exchange(ref _frameCount, 0);
        double seconds = _meterClock.Elapsed.TotalSeconds;
        _meterClock.Restart();
        if (seconds > 0)
        {
            MeasuredFrameRateChanged?.Invoke(this, frames / seconds);
        }
    }

    private void OnMediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        // Ignore late events from a session that was already replaced.
        if (!ReferenceEquals(sender, _mediaCapture))
        {
            return;
        }

        CameraFailed?.Invoke(this, new CameraFailureEventArgs(ActiveDevice?.Id, args.Message, args.Code));
    }

    private void OnPreviewFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        uint code = unchecked((uint)(args.ExtendedErrorCode?.HResult ?? 0));
        CameraFailed?.Invoke(this, new CameraFailureEventArgs(ActiveDevice?.Id, args.ErrorMessage, code));
    }

    private void RaiseDevicesChanged()
    {
        // The watcher first reports every existing camera as "Added"; only react to changes after that.
        if (_initialEnumerationDone)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Details of a camera failure, tagged with the device that was running when it happened.</summary>
public sealed class CameraFailureEventArgs : EventArgs
{
    public CameraFailureEventArgs(string? deviceId, string message, uint code)
    {
        DeviceId = deviceId;
        Message = message;
        Code = code;
    }

    public string? DeviceId { get; }

    public string Message { get; }

    public uint Code { get; }
}
