using System.Globalization;
using Windows.Media.MediaProperties;

namespace LensCraft.Models;

/// <summary>
/// One resolution + frame rate + pixel format combination the camera reports through
/// VideoDeviceController.GetAvailableMediaStreamProperties (spec §4).
/// </summary>
public sealed class CameraFormat
{
    public CameraFormat(VideoEncodingProperties properties)
    {
        Properties = properties;
        Width = properties.Width;
        Height = properties.Height;
        Subtype = properties.Subtype ?? string.Empty;
        FrameRate = properties.FrameRate.Denominator == 0
            ? 0
            : (double)properties.FrameRate.Numerator / properties.FrameRate.Denominator;
    }

    /// <summary>The exact object the driver gave us; passed back unchanged to SetMediaStreamPropertiesAsync.</summary>
    public VideoEncodingProperties Properties { get; }

    public uint Width { get; }

    public uint Height { get; }

    public double FrameRate { get; }

    /// <summary>Pixel format reported by the driver: NV12, MJPG, YUY2, ...</summary>
    public string Subtype { get; }

    /// <summary>Identity used to match formats across refreshes, e.g. "1920x1080@30/MJPG".</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}@{FrameRate:0.##}/{Subtype}");

    /// <summary>Left column in the picker, e.g. "1920 × 1080".</summary>
    public string ResolutionText => $"{Width} × {Height}";

    /// <summary>Right column in the picker, e.g. "30 fps, MJPG".</summary>
    public string DetailText => string.Create(CultureInfo.InvariantCulture, $"{FrameRate:0.##} fps, {Subtype}");

    public override string ToString() => $"{ResolutionText}, {DetailText}";
}
