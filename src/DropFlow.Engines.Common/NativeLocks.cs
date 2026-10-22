namespace DropFlow.Engines.Common;

/// <summary>
/// Process-wide locks for native libraries that are not thread-safe. PDFium (used by the PDF engine and by the AI engine's page
/// renderer) must never be entered from two threads at once, even for different documents, so every DropFlow call into it takes
/// <see cref="Pdfium"/>. Code outside DropFlow that uses PDFium in the same process (for example thumbnails in a UI) should take it too.
/// </summary>
public static class NativeLocks
{
    public static readonly object Pdfium = new();
}
