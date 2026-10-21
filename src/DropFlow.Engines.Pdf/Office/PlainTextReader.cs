namespace DropFlow.Engines.Pdf.Office;

/// <summary>A plain text file as a document: blank lines separate paragraphs, single line breaks inside a paragraph are kept.</summary>
internal static class PlainTextReader
{
    public static RichDocument Read(string path) => Parse(TextFile.Read(path), Path.GetFileNameWithoutExtension(path));

    public static RichDocument Parse(string text, string? title = null)
    {
        var blocks = new List<RichBlock>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new List<Run>();

        void Flush()
        {
            if (current.Count > 0) blocks.Add(new RParagraph(current));
            current = [];
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (current.Count > 0) current.Add(Run.LineBreak);
            current.Add(new Run(line));
        }
        Flush();
        return new RichDocument(title, blocks);
    }
}
