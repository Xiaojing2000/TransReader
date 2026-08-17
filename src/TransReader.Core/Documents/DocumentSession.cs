namespace TransReader.Core.Documents;

public sealed class DocumentSession
{
    public string? FilePath { get; private set; }
    public string DisplayName => FilePath is null ? "未打开 PDF" : Path.GetFileName(FilePath);
    public uint PageCount { get; private set; }
    public uint CurrentPageIndex { get; private set; }

    public void Open(string filePath, uint pageCount)
    {
        FilePath = filePath;
        PageCount = pageCount;
        CurrentPageIndex = 0;
    }

    public bool MoveTo(uint pageIndex)
    {
        if (PageCount == 0 || pageIndex >= PageCount || pageIndex == CurrentPageIndex)
        {
            return false;
        }

        CurrentPageIndex = pageIndex;
        return true;
    }
}

