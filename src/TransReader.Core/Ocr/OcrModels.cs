using System.Text.Json.Serialization;

namespace TransReader.Core.Ocr;

public sealed record OcrPage(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("blocks")] IReadOnlyList<OcrBlock> Blocks,
    [property: JsonPropertyName("engine_version")] string EngineVersion = "");

public sealed record OcrBlock(
    [property: JsonPropertyName("polygon")] IReadOnlyList<IReadOnlyList<int>> Polygon,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reading_order")] int ReadingOrder);

