using CommunityToolkit.Mvvm.ComponentModel;

namespace LensCraft.Models;

/// <summary>
/// One label-left / value-right row in the hardware panel (spec §5). Text and Detail are observable,
/// so live values (measured FPS, auto exposure) update in place without rebuilding the list.
/// </summary>
public sealed partial class HardwareInfoRow : ObservableObject
{
    public HardwareInfoRow(string label, string text, string detail = "")
    {
        Label = label;
        Text = text;
        Detail = detail;
    }

    public string Label { get; }

    [ObservableProperty]
    private string text = string.Empty;

    /// <summary>Optional second line under the value, e.g. a control's supported range.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string detail = string.Empty;

    public bool HasDetail => Detail.Length > 0;
}
