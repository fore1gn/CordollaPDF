using System.Globalization;
using System.IO;
using CordollaPDF.Interop;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CordollaPDF.ProFeatures.Extraction;

/// <summary>
/// Writes a <see cref="PdfExtractedDocument"/> to a .docx file, attempting to preserve
/// the original formatting: page size, fonts, sizes, weights, italics, colors, line
/// layout and paragraph structure.
/// </summary>
public static class DocxExporter
{
    // 1 PDF point = 20 twips (1 inch = 72 pt = 1440 twips).
    private const double PointsToTwips = 20d;

    public static void Export(PdfiumDocument document, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var service = new DeterministicPdfExtractionService();
        var extracted = service.Extract(document);
        Export(extracted, outputPath);
    }

    public static void Export(PdfExtractedDocument extracted, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(extracted);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var wordDocument = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = wordDocument.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = new Body();
        mainPart.Document.Append(body);

        var firstPage = extracted.Pages.FirstOrDefault(static page => page.PageBounds.Width > 0 && page.PageBounds.Height > 0);
        var pageWidthPoints = firstPage?.PageBounds.Width ?? 612d;
        var pageHeightPoints = firstPage?.PageBounds.Height ?? 792d;

        // Derive page margins from the actual content bounding box of a representative page.
        // Using the page with the most lines gives us the "real" margins for body content and
        // avoids short title pages producing outsized margins.
        var representativePage = extracted.Pages
            .Where(page => page.HasExtractableText)
            .OrderByDescending(page => page.Blocks.Sum(block => block.Lines.Count))
            .FirstOrDefault();

        var margins = ComputeMargins(representativePage, pageWidthPoints, pageHeightPoints);

        for (var pageIndex = 0; pageIndex < extracted.Pages.Count; pageIndex++)
        {
            var page = extracted.Pages[pageIndex];
            AppendPage(body, page, margins, isFirstPage: pageIndex == 0, isLastPage: pageIndex == extracted.Pages.Count - 1);
        }

        body.AppendChild(BuildSectionProperties(pageWidthPoints, pageHeightPoints, margins));

        mainPart.Document.Save();
    }

    private static void AppendPage(Body body, PdfExtractedPage page, PageMargins margins, bool isFirstPage, bool isLastPage)
    {
        if (!page.HasExtractableText)
        {
            if (!isLastPage)
            {
                body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            }

            return;
        }

        // The content area inside the DOCX page starts at x=marginLeft and y=marginTop-from-top.
        // In PDF user space the content's top is at PageBounds.Top - margins.TopPoints.
        var contentTopPdf = page.PageBounds.Top - margins.TopPoints;
        var contentLeftPdf = margins.LeftPoints;

        PdfTextLine? previousLine = null;
        var isFirstLineOnPage = true;

        foreach (var block in page.Blocks)
        {
            foreach (var line in block.Lines)
            {
                double spacingBeforePoints;
                if (isFirstLineOnPage)
                {
                    // Distance from the top edge of the content area down to this line's top.
                    spacingBeforePoints = Math.Max(0d, contentTopPdf - line.Bounds.Top);
                }
                else
                {
                    // Gap between the previous line's bottom (descender) and this line's top.
                    spacingBeforePoints = Math.Max(0d, previousLine!.Bounds.Bottom - line.Bounds.Top);
                }

                var paragraph = BuildParagraph(
                    line,
                    spacingBeforePoints,
                    contentLeftPdf,
                    insertPageBreakBefore: isFirstLineOnPage && !isFirstPage);
                body.AppendChild(paragraph);

                previousLine = line;
                isFirstLineOnPage = false;
            }
        }
    }

    private static Paragraph BuildParagraph(
        PdfTextLine line,
        double spacingBeforePoints,
        double pageLeft,
        bool insertPageBreakBefore)
    {
        var paragraph = new Paragraph();
        var paragraphProperties = new ParagraphProperties();

        // Left indent based on distance from the page's left edge (in twips).
        var indentPoints = Math.Max(0d, line.Bounds.Left - pageLeft);
        if (indentPoints > 1d)
        {
            paragraphProperties.Indentation = new Indentation
            {
                Left = ((int)Math.Round(indentPoints * PointsToTwips)).ToString(CultureInfo.InvariantCulture),
            };
        }

        var spacingBeforeTwips = Math.Max(0, (int)Math.Round(spacingBeforePoints * PointsToTwips));

        // Use exact line spacing so Word doesn't insert its own leading between our paragraphs —
        // each paragraph's height matches the PDF line height.
        var lineHeightPoints = Math.Max(1d, line.Bounds.Top - line.Bounds.Bottom);
        var lineHeightTwips = Math.Max(40, (int)Math.Round(lineHeightPoints * PointsToTwips));

        paragraphProperties.SpacingBetweenLines = new SpacingBetweenLines
        {
            Before = spacingBeforeTwips.ToString(CultureInfo.InvariantCulture),
            After = "0",
            Line = lineHeightTwips.ToString(CultureInfo.InvariantCulture),
            LineRule = LineSpacingRuleValues.Exact,
        };

        paragraph.AppendChild(paragraphProperties);

        if (insertPageBreakBefore)
        {
            paragraph.AppendChild(new Run(new Break { Type = BreakValues.Page }));
        }

        foreach (var run in line.Runs)
        {
            paragraph.AppendChild(BuildRun(run));
        }

        return paragraph;
    }

    private static Run BuildRun(PdfTextRun extractedRun)
    {
        var run = new Run();
        var runProperties = new RunProperties();
        var sanitizedText = SanitizeForXml(extractedRun.Text);

        var fontName = string.IsNullOrWhiteSpace(extractedRun.Style.FontName) || extractedRun.Style.FontName == "Unknown"
            ? "Calibri"
            : StripStyleSuffix(extractedRun.Style.FontName);

        runProperties.RunFonts = new RunFonts
        {
            Ascii = fontName,
            HighAnsi = fontName,
            ComplexScript = fontName,
            EastAsia = fontName,
        };

        // DOCX FontSize is expressed in half-points.
        var halfPoints = Math.Max(2, (int)Math.Round(extractedRun.Style.FontSizePoints * 2d));
        runProperties.FontSize = new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) };
        runProperties.FontSizeComplexScript = new FontSizeComplexScript { Val = halfPoints.ToString(CultureInfo.InvariantCulture) };

        if (extractedRun.Style.FontWeight >= 600 ||
            extractedRun.Style.FontName.Contains("bold", StringComparison.OrdinalIgnoreCase))
        {
            runProperties.Bold = new Bold();
            runProperties.BoldComplexScript = new BoldComplexScript();
        }

        if (extractedRun.Style.IsItalic)
        {
            runProperties.Italic = new Italic();
            runProperties.ItalicComplexScript = new ItalicComplexScript();
        }

        var colorHex = NormalizeColorHex(extractedRun.Style.FillColorHex);
        if (!string.Equals(colorHex, "000000", StringComparison.OrdinalIgnoreCase))
        {
            runProperties.Color = new Color { Val = colorHex };
        }

        run.AppendChild(runProperties);

        var text = new Text(sanitizedText)
        {
            Space = SpaceProcessingModeValues.Preserve,
        };
        run.AppendChild(text);

        return run;
    }

    /// <summary>
    /// Removes characters that are not valid in XML 1.0 so that OpenXml can serialize the text.
    /// PDFs frequently contain control characters, lone surrogates, and non-characters that would
    /// otherwise cause the DOCX writer to throw.
    /// </summary>
    private static string SanitizeForXml(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];

            // Correctly paired surrogates map to a valid supplementary code point — keep them.
            if (char.IsHighSurrogate(ch) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                var codePoint = char.ConvertToUtf32(ch, input[i + 1]);
                if (IsValidXmlCodePoint(codePoint))
                {
                    builder.Append(ch);
                    builder.Append(input[i + 1]);
                }

                i++;
                continue;
            }

            // Lone surrogates are invalid.
            if (char.IsSurrogate(ch))
            {
                continue;
            }

            if (IsValidXmlCodePoint(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsValidXmlCodePoint(int codePoint)
    {
        // XML 1.0 allowed ranges: #x9, #xA, #xD, #x20-#xD7FF, #xE000-#xFFFD, #x10000-#x10FFFF.
        return codePoint == 0x09
            || codePoint == 0x0A
            || codePoint == 0x0D
            || (codePoint >= 0x20 && codePoint <= 0xD7FF)
            || (codePoint >= 0xE000 && codePoint <= 0xFFFD)
            || (codePoint >= 0x10000 && codePoint <= 0x10FFFF);
    }

    private static SectionProperties BuildSectionProperties(double pageWidthPoints, double pageHeightPoints, PageMargins margins)
    {
        return new SectionProperties(
            new PageSize
            {
                Width = (UInt32Value)(uint)Math.Round(pageWidthPoints * PointsToTwips),
                Height = (UInt32Value)(uint)Math.Round(pageHeightPoints * PointsToTwips),
            },
            new PageMargin
            {
                Top = (int)Math.Round(margins.TopPoints * PointsToTwips),
                Right = (UInt32Value)(uint)Math.Round(margins.RightPoints * PointsToTwips),
                Bottom = (int)Math.Round(margins.BottomPoints * PointsToTwips),
                Left = (UInt32Value)(uint)Math.Round(margins.LeftPoints * PointsToTwips),
                Header = (UInt32Value)0U,
                Footer = (UInt32Value)0U,
                Gutter = (UInt32Value)0U,
            });
    }

    private static PageMargins ComputeMargins(PdfExtractedPage? representativePage, double pageWidthPoints, double pageHeightPoints)
    {
        if (representativePage is null || !representativePage.HasExtractableText)
        {
            return new PageMargins(72d, 72d, 72d, 72d);
        }

        var lines = representativePage.Blocks.SelectMany(block => block.Lines).ToList();
        if (lines.Count == 0)
        {
            return new PageMargins(72d, 72d, 72d, 72d);
        }

        var minLeft = lines.Min(line => line.Bounds.Left);
        var maxRight = lines.Max(line => line.Bounds.Right);
        var maxTop = lines.Max(line => line.Bounds.Top);
        var minBottom = lines.Min(line => line.Bounds.Bottom);

        var pageBounds = representativePage.PageBounds;
        var leftMargin = Math.Max(0d, minLeft - pageBounds.Left);
        var rightMargin = Math.Max(0d, pageBounds.Right - maxRight);
        var topMargin = Math.Max(0d, pageBounds.Top - maxTop);
        var bottomMargin = Math.Max(0d, minBottom - pageBounds.Bottom);

        // Clamp to something sane in case of pages with extreme content placement.
        leftMargin = Math.Clamp(leftMargin, 18d, pageWidthPoints / 3d);
        rightMargin = Math.Clamp(rightMargin, 18d, pageWidthPoints / 3d);
        topMargin = Math.Clamp(topMargin, 18d, pageHeightPoints / 3d);
        bottomMargin = Math.Clamp(bottomMargin, 18d, pageHeightPoints / 3d);

        return new PageMargins(leftMargin, topMargin, rightMargin, bottomMargin);
    }

    private readonly record struct PageMargins(double LeftPoints, double TopPoints, double RightPoints, double BottomPoints);

    private static string NormalizeColorHex(string rawHex)
    {
        if (string.IsNullOrWhiteSpace(rawHex))
        {
            return "000000";
        }

        var value = rawHex.StartsWith('#') ? rawHex[1..] : rawHex;
        return value.Length == 6 ? value.ToUpperInvariant() : "000000";
    }

    private static string StripStyleSuffix(string fontName)
    {
        // Remove common style suffixes so Word can find the base family (e.g. "Arial-BoldMT" → "Arial").
        string[] suffixes =
        [
            "-BoldItalicMT", "-BoldMT", "-ItalicMT", "PSMT", "MT",
            "-BoldItalic", "-BoldOblique", "-Oblique", "-Italic", "-Bold",
            ",BoldItalic", ",Bold", ",Italic",
        ];

        foreach (var suffix in suffixes)
        {
            if (fontName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return fontName[..^suffix.Length];
            }
        }

        return fontName;
    }
}
