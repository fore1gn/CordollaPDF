using CordollaPDF.Interop;

namespace CordollaPDF.ProFeatures.Extraction;

public sealed class DeterministicPdfExtractionService
{
    public PdfExtractedDocument Extract(PdfiumDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pages = new List<PdfExtractedPage>(document.PageCount);
        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            pages.Add(document.ExtractTextPage(pageIndex));
        }

        return new PdfExtractedDocument(
            document.Path,
            document.Name,
            pages);
    }
}
