namespace CordollaPDF.ProFeatures.Extraction;

public sealed record PdfExtractedDocument(
    string SourcePath,
    string DocumentName,
    IReadOnlyList<PdfExtractedPage> Pages)
{
    public int PageCount => Pages.Count;

    public bool HasExtractableText => Pages.Any(page => page.HasExtractableText);
}

public sealed record PdfExtractedPage(
    int PageIndex,
    PdfExtractionBounds PageBounds,
    IReadOnlyList<PdfTextBlock> Blocks)
{
    public int PageNumber => PageIndex + 1;

    public bool HasExtractableText => Blocks.Count > 0;

    public string Text => string.Join(Environment.NewLine + Environment.NewLine, Blocks.Select(block => block.Text));
}

public sealed record PdfTextBlock(
    int BlockIndex,
    PdfExtractionBounds Bounds,
    IReadOnlyList<PdfTextLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Text));
}

public sealed record PdfTextLine(
    int LineIndex,
    PdfExtractionBounds Bounds,
    IReadOnlyList<PdfTextRun> Runs)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

public sealed record PdfTextRun(
    int RunIndex,
    int StartCharIndex,
    int CharCount,
    string Text,
    PdfExtractionBounds Bounds,
    PdfTextStyle Style);

public sealed record PdfTextStyle(
    string FontName,
    double FontSizePoints,
    int FontWeight,
    bool IsItalic,
    string FillColorHex,
    double RotationDegrees);

public readonly record struct PdfExtractionBounds(
    double Left,
    double Bottom,
    double Right,
    double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public double MidX => (Left + Right) / 2d;

    public double MidY => (Top + Bottom) / 2d;

    public static PdfExtractionBounds Union(IEnumerable<PdfExtractionBounds> bounds)
    {
        var materialized = bounds.ToList();
        if (materialized.Count == 0)
        {
            return default;
        }

        var left = materialized.Min(item => item.Left);
        var bottom = materialized.Min(item => item.Bottom);
        var right = materialized.Max(item => item.Right);
        var top = materialized.Max(item => item.Top);
        return new PdfExtractionBounds(left, bottom, right, top);
    }
}
