namespace Filee.Engines.Translation;

public interface ITranslationService
{
    /// <summary>
    /// Translates a single text segment from <paramref name="fromLang"/> (or auto-detect) to <paramref name="toLang"/>.
    /// </summary>
    Task<string> TranslateTextAsync(string text, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default);

    /// <summary>
    /// Translates a batch of text segments, preserving ordering and any embedded inline formatting tags.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string toLang, string fromLang = "auto", CancellationToken cancellationToken = default);
}
