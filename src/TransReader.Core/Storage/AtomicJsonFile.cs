using System.Text.Json;

namespace TransReader.Core.Storage;

/// <summary>JSON 文件原子写入：先写临时文件再整体替换，避免写入中断留下半个文件。</summary>
public static class AtomicJsonFile
{
    /// <summary>将 value 序列化到临时文件后原子替换 path；失败时清理临时文件。</summary>
    public static async Task WriteAsync(
        string path,
        object value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
