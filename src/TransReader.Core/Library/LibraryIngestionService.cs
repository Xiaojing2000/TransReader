using System.Security.Cryptography;

namespace TransReader.Core.Library;

public sealed class LibraryIngestionService
{
    private readonly string _libraryRoot;
    private readonly LibraryRepository _repository;
    private readonly SemaphoreSlim _ingestionGate = new(1, 1);

    public LibraryIngestionService(string libraryRoot, LibraryRepository repository)
    {
        _libraryRoot = libraryRoot;
        _repository = repository;
    }

    public async Task<LibraryImportResult> EnsureImportedAsync(
        string sourcePath, uint pageCount, CancellationToken cancellationToken = default)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var managed = await _repository.FindByManagedPathAsync(fullSourcePath, cancellationToken);
        if (managed is not null)
            return new LibraryImportResult(managed, WasCreated: false, WasDuplicate: true);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("PDF 文件不存在。", fullSourcePath);
        if (!Path.GetExtension(fullSourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文献库目前只支持 PDF 文件。");
        if (pageCount == 0) throw new InvalidDataException("PDF 没有可读取的页面。");

        await _ingestionGate.WaitAsync(cancellationToken);
        var temporaryPath = string.Empty;
        try
        {
            Directory.CreateDirectory(Path.Combine(_libraryRoot, "objects"));
            var temporaryDirectory = Path.Combine(_libraryRoot, "temp");
            Directory.CreateDirectory(temporaryDirectory);
            temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.importing");
            string hash;
            long length;
            await using (var input = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                }
                await output.FlushAsync(cancellationToken);
                hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                length = output.Length;
            }

            var existing = await _repository.FindByHashAsync(hash, cancellationToken);
            if (existing is not null)
            {
                await _repository.AddSourceAsync(existing.Id, fullSourcePath, cancellationToken);
                File.Delete(temporaryPath);
                temporaryPath = string.Empty;
                return new LibraryImportResult(await _repository.FindByIdAsync(existing.Id, cancellationToken) ?? existing,
                    WasCreated: false, WasDuplicate: true);
            }

            var objectDirectory = Path.Combine(_libraryRoot, "objects", hash[..2]);
            Directory.CreateDirectory(objectDirectory);
            var managedPath = Path.Combine(objectDirectory, $"{hash}.pdf");
            if (!File.Exists(managedPath)) File.Move(temporaryPath, managedPath);
            else File.Delete(temporaryPath);
            temporaryPath = string.Empty;
            var document = await _repository.AddImportedDocumentAsync(hash, managedPath, fullSourcePath,
                Path.GetFileNameWithoutExtension(fullSourcePath), pageCount, length, cancellationToken);
            return new LibraryImportResult(document, WasCreated: true, WasDuplicate: false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
            _ingestionGate.Release();
        }
    }
}
