using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LensCraft.Models;
using Windows.Devices.Enumeration;

namespace LensCraft.Services;

/// <summary>
/// Turns what Windows reports about the active camera into panel rows and a copyable text report
/// (spec §5). Reads hardware only through CameraService (spec §2). A field Windows does not report
/// is omitted, never shown as N/A.
/// </summary>
public sealed class HardwareInfoService
{
    private static readonly Regex VidPattern = new(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PidPattern = new(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly CameraService _camera;

    public HardwareInfoService(CameraService camera)
    {
        _camera = camera;
    }

    /// <summary>Identity group: DeviceInformation.Name, VID:PID parsed from DeviceInformation.Id, enclosure panel.</summary>
    public IReadOnlyList<HardwareInfoRow> GetIdentityRows() => BuildIdentityRows(includeFullDeviceId: false);

    /// <summary>Active format group: VideoDeviceController.GetMediaStreamProperties as VideoEncodingProperties.</summary>
    public IReadOnlyList<HardwareInfoRow> GetActiveFormatRows()
    {
        CameraFormat? format = _camera.ActiveFormat;
        if (format is null)
        {
            return [];
        }

        return
        [
            new HardwareInfoRow("Resolution", format.ResolutionText),
            new HardwareInfoRow("Frame rate", $"{Number(format.FrameRate)} fps"),
            new HardwareInfoRow("Pixel format", format.Subtype),
        ];
    }

    /// <summary>Controls group: only controls whose Capabilities.Supported is true.</summary>
    public IReadOnlyList<HardwareInfoRow> GetControlRows()
    {
        List<HardwareInfoRow> rows = _camera.GetControlReadings()
            .Select(reading => new HardwareInfoRow(reading.Name, DescribeValue(reading), DescribeRange(reading)))
            .ToList();

        // Diagnostic: when on, the camera may lower its frame rate in low light.
        if (_camera.GetExposurePriority() is bool exposurePriority)
        {
            rows.Add(new HardwareInfoRow("Exposure priority", exposurePriority ? "on" : "off"));
        }

        return rows;
    }

    /// <summary>Session group: FPS counted on MediaFrameReader.FrameArrived, averaged over 1 s.</summary>
    public IReadOnlyList<HardwareInfoRow> GetSessionRows(double? measuredFps)
    {
        var rows = new List<HardwareInfoRow>();
        if (measuredFps is double fps)
        {
            string selected = _camera.ActiveFormat is { } format ? $"selected {Number(format.FrameRate)} fps" : string.Empty;
            rows.Add(new HardwareInfoRow("Measured FPS", $"{fps.ToString("0.0", CultureInfo.InvariantCulture)} fps", selected));
        }

        // Diagnostic: the rate the frame source reports for itself, to compare with Measured FPS.
        if (_camera.PreviewSourceFrameRate is double driverFps)
        {
            rows.Add(new HardwareInfoRow("Driver frame rate", $"{Number(driverFps)} fps"));
        }

        return rows;
    }

    /// <summary>Plain-text copy of the whole panel, for webcam bug reports (spec §5 copy icon).</summary>
    public string BuildReport(double? measuredFps)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"LensCraft camera details, {DateTime.Now:yyyy-MM-dd HH:mm}");

        AppendGroup(report, "Identity", BuildIdentityRows(includeFullDeviceId: true));
        AppendGroup(report, "Active format", GetActiveFormatRows());

        IReadOnlyList<CameraFormat> formats = _camera.GetAvailableFormats();
        if (formats.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"Capabilities ({formats.Count} formats)");
            foreach (CameraFormat format in formats)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {format}");
            }
        }

        AppendGroup(report, "Controls", GetControlRows());
        AppendGroup(report, "Session", GetSessionRows(measuredFps));

        if (_camera.ActiveDevice is null)
        {
            report.AppendLine();
            report.AppendLine("No camera is running.");
        }

        return report.ToString();
    }

    private List<HardwareInfoRow> BuildIdentityRows(bool includeFullDeviceId)
    {
        var rows = new List<HardwareInfoRow>();
        CameraDeviceInfo? device = _camera.ActiveDevice;
        if (device is null)
        {
            return rows;
        }

        rows.Add(new HardwareInfoRow("Name", device.Name));

        Match vid = VidPattern.Match(device.Id);
        Match pid = PidPattern.Match(device.Id);
        bool hasVidPid = vid.Success && pid.Success;
        if (hasVidPid)
        {
            rows.Add(new HardwareInfoRow("VID:PID", $"{vid.Groups[1].Value.ToUpperInvariant()}:{pid.Groups[1].Value.ToUpperInvariant()}"));
        }

        // The full ID is ~100 characters; the panel shows it only when there is no VID:PID to identify the camera.
        if (includeFullDeviceId || !hasVidPid)
        {
            rows.Add(new HardwareInfoRow("Device ID", device.Id));
        }

        if (DescribeLocation(device.Location) is { } location)
        {
            rows.Add(new HardwareInfoRow("Location", location));
        }

        return rows;
    }

    private static string? DescribeLocation(Panel? panel) => panel switch
    {
        Panel.Front => "Front panel",
        Panel.Back => "Back panel",
        Panel.Top => "Top panel",
        Panel.Bottom => "Bottom panel",
        Panel.Left => "Left panel",
        Panel.Right => "Right panel",
        _ => null, // not reported: omitted rather than guessed (spec §5)
    };

    private static string DescribeValue(CameraControlReading reading)
    {
        string auto = reading.IsAuto == true ? " (auto)" : string.Empty;
        return reading.Value is double value ? Number(value) + auto : (reading.IsAuto == true ? "auto" : "not readable");
    }

    private static string DescribeRange(CameraControlReading reading)
        => $"{Number(reading.Min)} to {Number(reading.Max)}, step {Number(reading.Step)}";

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AppendGroup(StringBuilder report, string title, IReadOnlyList<HardwareInfoRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine(title);
        foreach (HardwareInfoRow row in rows)
        {
            string detail = row.HasDetail ? $" | {row.Detail}" : string.Empty;
            report.AppendLine(CultureInfo.InvariantCulture, $"  {row.Label}: {row.Text}{detail}");
        }
    }
}
