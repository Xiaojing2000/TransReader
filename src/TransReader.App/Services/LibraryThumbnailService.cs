using TransReader.Core.Library;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TransReader.App.Services;

internal sealed class LibraryThumbnailService
{
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public async Task<bool> EnsureAsync(LibraryDocument document, CancellationToken cancellationToken = default)
    {
        if (File.Exists(document.ThumbnailPath)) return false;
        lock (_active)
        {
            if (!_active.Add(document.ContentHash)) return false;
        }

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = string.Empty;
        try
        {
            if (File.Exists(document.ThumbnailPath)) return false;
            var file = await StorageFile.GetFileFromPathAsync(document.ManagedPath);
            var pdf = await PdfDocument.LoadFromFileAsync(file);
            if (pdf.PageCount == 0) return false;
            using var page = pdf.GetPage(0);
            var scale = Math.Min(180d / page.Size.Width, 240d / page.Size.Height);
            var bytes = await PdfPageRenderer.RenderBytesAsync(
                pdf,
                0,
                (uint)Math.Max(1, page.Size.Width * scale),
                (uint)Math.Max(1, page.Size.Height * scale),
                BitmapEncoder.JpegEncoderId,
                cancellationToken);

            var directory = Path.GetDirectoryName(document.ThumbnailPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"{document.ContentHash}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            if (!File.Exists(document.ThumbnailPath)) File.Move(temporaryPath, document.ThumbnailPath);
            else File.Delete(temporaryPath);
            temporaryPath = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"生成文献缩略图失败 {document.Id}", ex);
            return false;
        }
        finally
        {
            if (temporaryPath.Length > 0 && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
            _gate.Release();
            lock (_active) _active.Remove(document.ContentHash);
        }
    }
}
