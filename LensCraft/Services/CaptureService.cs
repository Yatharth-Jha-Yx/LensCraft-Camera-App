using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LensCraft.Services;

/// <summary>
/// Saves photos to Pictures\LensCraft (spec §4). It never touches MediaCapture directly;
/// the shutter goes through CameraService (spec §2).
/// </summary>
public sealed class CaptureService
{
    private const string FolderName = "LensCraft";
    private static readonly string[] PhotoExtensions = [".jpg", ".jpeg"];

    private readonly CameraService _camera;

    public CaptureService(CameraService camera)
    {
        _camera = camera;
    }

    /// <summary>
    /// Takes a photo and saves it. By default the saved photo is NOT mirrored, so it matches what
    /// other people see (spec §4, mirror nuance). Pass true to flip it like the preview.
    /// </summary>
    public async Task<StorageFile> CapturePhotoAsync(bool mirrorSavedPhoto)
    {
        StorageFolder folder = await GetSaveFolderAsync();
        StorageFile file = await folder.CreateFileAsync(
            $"LensCraft_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg",
            CreationCollisionOption.GenerateUniqueName);

        try
        {
            await _camera.CapturePhotoToStorageFileAsync(file);
            if (mirrorSavedPhoto)
            {
                await FlipHorizontallyAsync(file);
            }

            return file;
        }
        catch
        {
            await TryDeleteEmptyFileAsync(file);
            throw;
        }
    }

    /// <summary>The newest saved photos, newest first, for the photo strip.</summary>
    public async Task<IReadOnlyList<StorageFile>> GetRecentCapturesAsync(int count)
    {
        StorageFolder folder = await GetSaveFolderAsync();
        IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
        return files
            .Where(file => PhotoExtensions.Contains(file.FileType, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(file => file.DateCreated)
            .Take(count)
            .ToList();
    }

    /// <summary>Pictures\LensCraft, created on first use. Follows OneDrive folder redirection.</summary>
    public async Task<StorageFolder> GetSaveFolderAsync()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FolderName);
        Directory.CreateDirectory(path);
        return await StorageFolder.GetFolderFromPathAsync(path);
    }

    /// <summary>Re-encodes the JPEG flipped left-to-right (Windows.Graphics.Imaging transcode pattern).</summary>
    private static async Task FlipHorizontallyAsync(StorageFile file)
    {
        using IRandomAccessStream fileStream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(fileStream);

        using var flipped = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(flipped, decoder);
        encoder.BitmapTransform.Flip = BitmapFlip.Horizontal;
        await encoder.FlushAsync();

        flipped.Seek(0);
        fileStream.Seek(0);
        fileStream.Size = 0;
        await RandomAccessStream.CopyAsync(flipped, fileStream);
        await fileStream.FlushAsync();
    }

    private static async Task TryDeleteEmptyFileAsync(StorageFile file)
    {
        try
        {
            // The file was created a moment ago and holds no photo; skip the Recycle Bin.
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LensCraft: could not remove unfinished file {file.Path}: {ex.Message}");
        }
    }
}
