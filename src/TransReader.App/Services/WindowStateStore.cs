using System.Text.Json;

namespace TransReader.App.Services;

/// <summary>Persists the main window size and position.</summary>
internal sealed class WindowStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public WindowStateStore(string filePath)
    {
        _filePath = filePath;
    }

    public WindowState? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }
            var state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(_filePath), SerializerOptions);
            return state is { Width: >= 800, Height: >= 500 } ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(int x, int y, int width, int height)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(new WindowState(x, y, width, height), SerializerOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record WindowState(int X, int Y, int Width, int Height);
