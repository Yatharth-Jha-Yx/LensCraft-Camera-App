using Windows.Devices.Enumeration;

namespace LensCraft.Models;

/// <summary>
/// One attached camera, as reported by DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).
/// A record, so two instances with the same values compare equal (handy when the list refreshes).
/// </summary>
public sealed record CameraDeviceInfo(string Id, string Name, Panel? Location)
{
    /// <summary>Text shown in the camera picker, e.g. "Integrated Camera (front)".</summary>
    public string DisplayName => Location switch
    {
        Panel.Front => $"{Name} (front)",
        Panel.Back => $"{Name} (back)",
        _ => Name,
    };

    public static CameraDeviceInfo FromDeviceInformation(DeviceInformation device)
        => new(device.Id, device.Name, device.EnclosureLocation?.Panel);
}
