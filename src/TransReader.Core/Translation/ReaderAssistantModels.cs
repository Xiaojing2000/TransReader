namespace TransReader.Core.Translation;

public enum ReaderQuestionMode
{
    Explain,
    Ask,
    FollowUp
}

public sealed record ReaderSelectionContext(
    string DocumentKey,
    uint PageNumber,
    string SelectedText,
    string SurroundingText,
    string StructureType);

public sealed record ReaderAssistantMessage(
    string Id,
    string Role,
    string Markdown,
    DateTimeOffset CreatedAt,
    bool IsComplete = true,
    string Model = "");

public sealed record ReaderAssistantTopic(
    string Id,
    ReaderSelectionContext Selection,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ReaderAssistantMessage> Messages);

public sealed record ReaderQuestionRequest(
    ReaderQuestionMode Mode,
    string Question,
    ReaderSelectionContext Selection,
    string PageTranslation,
    string PageSourceText,
    DocumentTranslationContext DocumentContext,
    IReadOnlyList<ReaderAssistantMessage> History,
    ReadOnlyMemory<byte> PageImage,
    string ImageMediaType);

public sealed record ReaderAnswerUpdate(
    string TopicId,
    string Markdown,
    bool IsFinal,
    long ContentVersion);

