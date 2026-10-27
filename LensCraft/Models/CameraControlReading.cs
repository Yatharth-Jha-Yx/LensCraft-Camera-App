namespace LensCraft.Models;

/// <summary>
/// One supported camera control (exposure, white balance, ...) as reported by
/// VideoDeviceController.&lt;Control&gt;.Capabilities plus TryGetValue / TryGetAuto (spec §5).
/// Values are raw driver units, exactly as Windows reports them.
/// </summary>
public sealed record CameraControlReading(
    string Name,
    double? Value,
    bool? IsAuto,
    double Min,
    double Max,
    double Step);
