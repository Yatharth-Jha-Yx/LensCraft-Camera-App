using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LensCraft.Models;

/// <summary>A saved photo plus the small decoded thumbnail shown in the photo strip.</summary>
public sealed class CapturedPhoto
{
    // 64 px thumbnails, decoded at 128 px so they stay sharp on 200% display scaling.
    private const int ThumbnailDecodeWidth = 128;

    private CapturedPhoto(StorageFile file, BitmapImage thumbnail)
    {
        File = file;
        Thumbnail = thumbnail;
    }

    public StorageFile File { get; }

    public BitmapImage Thumbnail { get; }

    public string Name => File.Name;

    /// <summary>Must be called on the UI thread (BitmapImage is a UI object).</summary>
    public static async Task<CapturedPhoto> CreateAsync(StorageFile file)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = ThumbnailDecodeWidth };
        using (IRandomAccessStream stream = await file.OpenReadAsync())
        {
            await bitmap.SetSourceAsync(stream);
        }

        return new CapturedPhoto(file, bitmap);
    }
}
