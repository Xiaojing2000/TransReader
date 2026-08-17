using TransReader.Core.Library;

namespace TransReader.App.Services;

internal sealed record LibraryNavigationItem(
    string Id,
    string Label,
    int Count,
    LibraryNavigationKind Kind,
    string? FolderId = null,
    int Depth = 0)
{
    public string DisplayLabel => $"{new string('　', Depth)}{Label}";
}
