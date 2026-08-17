using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TransReader.App.Services;

/// <summary>PDF 页面渲染共享实现（按最大边等比缩放或按指定尺寸，输出指定编码格式）。</summary>
internal static class PdfPageRenderer
{
    /// <summary>按最大边等比缩放渲染为指定编码的流（游标已归零）。</summary>
    public static async Task<InMemoryRandomAccessStream> RenderToStreamAsync(
        PdfDocument document,
        uint pageIndex,
        double maxDimension,
        Guid encoderId,
        CancellationToken cancellationToken)
    {
        using var page = document.GetPage(pageIndex);
        var scale = Math.Min(maxDimension / page.Size.Width, maxDimension / page.Size.Height);
        return await RenderToStreamAsync(
            document,
            pageIndex,
            (uint)Math.Max(1, page.Size.Width * scale),
            (uint)Math.Max(1, page.Size.Height * scale),
            encoderId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按指定目标尺寸渲染为指定编码的流（游标已归零）。</summary>
    public static async Task<InMemoryRandomAccessStream> RenderToStreamAsync(
        PdfDocument document,
        uint pageIndex,
        uint destinationWidth,
        uint destinationHeight,
        Guid encoderId,
        CancellationToken cancellationToken)
    {
        using var page = document.GetPage(pageIndex);
        var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = destinationWidth,
            DestinationHeight = destinationHeight,
            BitmapEncoderId = encoderId
        }).AsTask(cancellationToken);
        stream.Seek(0);
        return stream;
    }

    /// <summary>渲染为编码后的字节数组。</summary>
    public static async Task<byte[]> RenderBytesAsync(
        PdfDocument document,
        uint pageIndex,
        double maxDimension,
        Guid encoderId,
        CancellationToken cancellationToken)
    {
        using var stream = await RenderToStreamAsync(
            document, pageIndex, maxDimension, encoderId, cancellationToken).ConfigureAwait(false);
        return await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按指定目标尺寸渲染为编码后的字节数组。</summary>
    public static async Task<byte[]> RenderBytesAsync(
        PdfDocument document,
        uint pageIndex,
        uint destinationWidth,
        uint destinationHeight,
        Guid encoderId,
        CancellationToken cancellationToken)
    {
        using var stream = await RenderToStreamAsync(
            document, pageIndex, destinationWidth, destinationHeight, encoderId, cancellationToken).ConfigureAwait(false);
        return await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取流中全部字节（游标先归零）。</summary>
    public static async Task<byte[]> ReadBytesAsync(
        InMemoryRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        stream.Seek(0);
        var bytes = new byte[checked((int)stream.Size)];
        var buffer = new Windows.Storage.Streams.Buffer((uint)bytes.Length);
        await stream.ReadAsync(buffer, (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken);
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>把位图编码为 JPEG 字节（用于页面显示与多模态 API 上传）。</summary>
    public static async Task<byte[]> EncodeJpegAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
        return await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>将流解码为 BGRA8 位图（游标先归零）。</summary>
    public static async Task<SoftwareBitmap> DecodeBitmapAsync(
        InMemoryRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
    }
}
