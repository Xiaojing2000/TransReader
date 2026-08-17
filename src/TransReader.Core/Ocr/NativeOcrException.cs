namespace TransReader.Core.Ocr;

public sealed class NativeOcrException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

