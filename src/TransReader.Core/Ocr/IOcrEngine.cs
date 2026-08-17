namespace TransReader.Core.Ocr;

/// <summary>OCR engine abstraction: in-process (P/Invoke) or worker-process host.</summary>
public interface IOcrEngine : IDisposable
{
    OcrPage Recognize(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride);
}
