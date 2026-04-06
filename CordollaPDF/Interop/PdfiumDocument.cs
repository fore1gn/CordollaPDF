using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;
using CordollaPDF.Models;
using CordollaPDF.ProFeatures.Extraction;

namespace CordollaPDF.Interop;

public sealed class PdfiumDocument : IDisposable
{
    private static readonly object LibraryLock = new();
    private static int _libraryUsers;

    private readonly object _syncRoot = new();
    private readonly byte[] _documentBytes;
    private readonly GCHandle _documentHandle;
    private readonly Dictionary<int, string> _pageTextCache = [];
    private bool _disposed;

    public PdfiumDocument(string path)
    {
        EnsureLibrary();

        Path = path;
        Name = System.IO.Path.GetFileName(path);
        _documentBytes = File.ReadAllBytes(path);
        _documentHandle = GCHandle.Alloc(_documentBytes, GCHandleType.Pinned);

        Handle = fpdfview.FPDF_LoadMemDocument64(
            _documentHandle.AddrOfPinnedObject(),
            (ulong)_documentBytes.LongLength,
            null);

        if (Handle == default)
        {
            _documentHandle.Free();
            ReleaseLibrary();
            throw new InvalidOperationException($"PDFium failed to open the document. Error code: {fpdfview.FPDF_GetLastError()}");
        }

        PageCount = fpdfview.FPDF_GetPageCount(Handle);
        PageSizes = LoadPageSizes();
        Bookmarks = LoadBookmarks();
    }

    public string Path { get; }

    public string Name { get; }

    public FpdfDocumentT Handle { get; }

    public int PageCount { get; }

    public IReadOnlyList<Size> PageSizes { get; }

    public IReadOnlyList<PdfOutlineNode> Bookmarks { get; }

    public ImageSource RenderPage(int pageIndex, int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            var page = fpdfview.FPDF_LoadPage(Handle, pageIndex);
            if (page == default)
            {
                throw new InvalidOperationException($"Unable to load page {pageIndex + 1}.");
            }

            try
            {
                var bitmap = fpdfview.FPDFBitmapCreate(pixelWidth, pixelHeight, 1);
                if (bitmap == default)
                {
                    throw new InvalidOperationException("Unable to allocate a PDFium bitmap.");
                }

                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                    fpdfview.FPDF_RenderPageBitmap(
                        bitmap,
                        page,
                        0,
                        0,
                        pixelWidth,
                        pixelHeight,
                        0,
                        (int)(RenderFlags.RenderAnnotations | RenderFlags.OptimizeTextForLcd));

                    return CreateBitmapSource(bitmap, pixelWidth, pixelHeight);
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public PdfTextSelectionResult? SelectText(int pageIndex, Point startDisplayPoint, Point endDisplayPoint, Size displaySize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pageIndex < 0 || pageIndex >= PageCount || displaySize.Width <= 0 || displaySize.Height <= 0)
        {
            return null;
        }

        lock (_syncRoot)
        {
            var page = fpdfview.FPDF_LoadPage(Handle, pageIndex);
            if (page == default)
            {
                return null;
            }

            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage == default)
                {
                    return null;
                }

                try
                {
                    var startIndex = FindNearestCharacterIndex(textPage, pageIndex, startDisplayPoint, displaySize);
                    var endIndex = FindNearestCharacterIndex(textPage, pageIndex, endDisplayPoint, displaySize);
                    if (startIndex < 0 || endIndex < 0)
                    {
                        return null;
                    }

                    if (startIndex > endIndex)
                    {
                        (startIndex, endIndex) = (endIndex, startIndex);
                    }

                    var count = (endIndex - startIndex) + 1;
                    if (count <= 0)
                    {
                        return null;
                    }

                    var buffer = new ushort[count + 1];
                    var written = fpdf_text.FPDFTextGetText(textPage, startIndex, count, ref buffer[0]);
                    if (written <= 1)
                    {
                        return null;
                    }

                    var text = new string(buffer.Take(written - 1).Select(value => (char)value).ToArray()).Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return null;
                    }

                    var rectCount = fpdf_text.FPDFTextCountRects(textPage, startIndex, count);
                    var rects = new List<TextSelectionRect>();

                    for (var i = 0; i < rectCount; i++)
                    {
                        double left = 0;
                        double top = 0;
                        double right = 0;
                        double bottom = 0;

                        if (fpdf_text.FPDFTextGetRect(textPage, i, ref left, ref top, ref right, ref bottom) == 0)
                        {
                            continue;
                        }

                        rects.Add(ConvertPageRectToDisplayRect(pageIndex, left, top, right, bottom, displaySize));
                    }

                    if (rects.Count == 0)
                    {
                        return null;
                    }

                    return new PdfTextSelectionResult(text, rects);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public string GetPageText(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            return string.Empty;
        }

        lock (_syncRoot)
        {
            if (_pageTextCache.TryGetValue(pageIndex, out var cached))
            {
                return cached;
            }

            var page = fpdfview.FPDF_LoadPage(Handle, pageIndex);
            if (page == default)
            {
                return string.Empty;
            }

            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage == default)
                {
                    return string.Empty;
                }

                try
                {
                    var charCount = fpdf_text.FPDFTextCountChars(textPage);
                    if (charCount <= 0)
                    {
                        _pageTextCache[pageIndex] = string.Empty;
                        return string.Empty;
                    }

                    var buffer = new ushort[charCount + 1];
                    var written = fpdf_text.FPDFTextGetText(textPage, 0, charCount, ref buffer[0]);
                    if (written <= 1)
                    {
                        _pageTextCache[pageIndex] = string.Empty;
                        return string.Empty;
                    }

                    var text = new string(buffer.Take(written - 1).Select(value => (char)value).ToArray());
                    _pageTextCache[pageIndex] = text;
                    return text;
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public PdfExtractedPage ExtractTextPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            return new PdfExtractedPage(pageIndex, default, []);
        }

        var pageSize = PageSizes[pageIndex];
        var pageBounds = new PdfExtractionBounds(0, 0, pageSize.Width, pageSize.Height);

        lock (_syncRoot)
        {
            var page = fpdfview.FPDF_LoadPage(Handle, pageIndex);
            if (page == default)
            {
                return new PdfExtractedPage(pageIndex, pageBounds, []);
            }

            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage == default)
                {
                    return new PdfExtractedPage(pageIndex, pageBounds, []);
                }

                try
                {
                    var chars = ExtractCharacters(textPage);
                    if (chars.Count == 0)
                    {
                        return new PdfExtractedPage(pageIndex, pageBounds, []);
                    }

                    var lines = BuildLines(chars);
                    var blocks = BuildBlocks(lines);
                    return new PdfExtractedPage(pageIndex, pageBounds, blocks);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public PdfTextSelectionResult? SelectTextByTextRange(int pageIndex, int textStartIndex, int textLength, Size displaySize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pageIndex < 0 || pageIndex >= PageCount || textStartIndex < 0 || textLength <= 0 || displaySize.Width <= 0 || displaySize.Height <= 0)
        {
            return null;
        }

        lock (_syncRoot)
        {
            var page = fpdfview.FPDF_LoadPage(Handle, pageIndex);
            if (page == default)
            {
                return null;
            }

            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage == default)
                {
                    return null;
                }

                try
                {
                    var charStartIndex = fpdf_searchex.FPDFTextGetCharIndexFromTextIndex(textPage, textStartIndex);
                    var charEndIndex = fpdf_searchex.FPDFTextGetCharIndexFromTextIndex(textPage, textStartIndex + textLength - 1);
                    if (charStartIndex < 0 || charEndIndex < 0 || charEndIndex < charStartIndex)
                    {
                        return null;
                    }

                    var charCount = (charEndIndex - charStartIndex) + 1;
                    var buffer = new ushort[textLength + 1];
                    var written = fpdf_text.FPDFTextGetText(textPage, charStartIndex, charCount, ref buffer[0]);
                    if (written <= 1)
                    {
                        return null;
                    }

                    var text = new string(buffer.Take(written - 1).Select(value => (char)value).ToArray()).Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return null;
                    }

                    var rectCount = fpdf_text.FPDFTextCountRects(textPage, charStartIndex, charCount);
                    var rects = new List<TextSelectionRect>();
                    for (var i = 0; i < rectCount; i++)
                    {
                        double left = 0;
                        double top = 0;
                        double right = 0;
                        double bottom = 0;

                        if (fpdf_text.FPDFTextGetRect(textPage, i, ref left, ref top, ref right, ref bottom) == 0)
                        {
                            continue;
                        }

                        rects.Add(ConvertPageRectToDisplayRect(pageIndex, left, top, right, bottom, displaySize));
                    }

                    if (rects.Count == 0)
                    {
                        return null;
                    }

                    return new PdfTextSelectionResult(text, rects);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_syncRoot)
        {
            fpdfview.FPDF_CloseDocument(Handle);
        }

        if (_documentHandle.IsAllocated)
        {
            _documentHandle.Free();
        }

        ReleaseLibrary();
    }

    private IReadOnlyList<Size> LoadPageSizes()
    {
        var sizes = new List<Size>(PageCount);

        for (var i = 0; i < PageCount; i++)
        {
            lock (_syncRoot)
            {
                var page = fpdfview.FPDF_LoadPage(Handle, i);
                if (page == default)
                {
                    sizes.Add(new Size(800, 1100));
                    continue;
                }

                try
                {
                    var width = fpdfview.FPDF_GetPageWidthF(page);
                    var height = fpdfview.FPDF_GetPageHeightF(page);
                    sizes.Add(new Size(width, height));
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }

        return sizes;
    }

    private IReadOnlyList<PdfOutlineNode> LoadBookmarks()
    {
        var items = new List<PdfOutlineNode>();
        lock (_syncRoot)
        {
            var bookmark = fpdf_doc.FPDFBookmarkGetFirstChild(Handle, default);

            while (bookmark != default)
            {
                items.Add(LoadBookmarkNode(bookmark));
                bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(Handle, bookmark);
            }
        }

        return items;
    }

    private PdfOutlineNode LoadBookmarkNode(FpdfBookmarkT bookmark)
    {
        var title = ReadBookmarkTitle(bookmark);
        var pageIndex = ResolveBookmarkPageIndex(bookmark);
        var node = new PdfOutlineNode(string.IsNullOrWhiteSpace(title) ? $"Page {pageIndex + 1}" : title, pageIndex + 1);

        var child = fpdf_doc.FPDFBookmarkGetFirstChild(Handle, bookmark);
        while (child != default)
        {
            node.Children.Add(LoadBookmarkNode(child));
            child = fpdf_doc.FPDFBookmarkGetNextSibling(Handle, child);
        }

        return node;
    }

    private string ReadBookmarkTitle(FpdfBookmarkT bookmark)
    {
        var byteCount = (int)fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (byteCount <= 2)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, (ulong)byteCount);
            var bytes = new byte[byteCount - 2];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int ResolveBookmarkPageIndex(FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(Handle, bookmark);
        if (dest != default)
        {
            var pageIndex = fpdf_doc.FPDFDestGetDestPageIndex(Handle, dest);
            if (pageIndex >= 0)
            {
                return pageIndex;
            }
        }

        var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
        if (action != default)
        {
            var actionDest = fpdf_doc.FPDFActionGetDest(Handle, action);
            if (actionDest != default)
            {
                var actionPageIndex = fpdf_doc.FPDFDestGetDestPageIndex(Handle, actionDest);
                if (actionPageIndex >= 0)
                {
                    return actionPageIndex;
                }
            }
        }

        return 0;
    }

    private static ImageSource CreateBitmapSource(FpdfBitmapT bitmap, int pixelWidth, int pixelHeight)
    {
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var bytes = new byte[stride * pixelHeight];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);

        var source = BitmapSource.Create(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bytes,
            stride);

        source.Freeze();
        return source;
    }

    private int FindNearestCharacterIndex(FpdfTextpageT textPage, int pageIndex, Point displayPoint, Size displaySize)
    {
        var pageSize = PageSizes[pageIndex];
        var pdfPoint = DisplayToPdfPoint(displayPoint, pageSize, displaySize);

        foreach (var tolerance in new[] { 2d, 5d, 8d, 12d })
        {
            var index = fpdf_text.FPDFTextGetCharIndexAtPos(textPage, pdfPoint.X, pdfPoint.Y, tolerance, tolerance);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static Point DisplayToPdfPoint(Point displayPoint, Size pageSize, Size displaySize)
    {
        var clampedX = Math.Clamp(displayPoint.X, 0, displaySize.Width);
        var clampedY = Math.Clamp(displayPoint.Y, 0, displaySize.Height);
        var pdfX = (clampedX / displaySize.Width) * pageSize.Width;
        var pdfY = pageSize.Height - ((clampedY / displaySize.Height) * pageSize.Height);
        return new Point(pdfX, pdfY);
    }

    private TextSelectionRect ConvertPageRectToDisplayRect(int pageIndex, double left, double top, double right, double bottom, Size displaySize)
    {
        var pageSize = PageSizes[pageIndex];
        var displayLeft = (left / pageSize.Width) * displaySize.Width;
        var displayTop = ((pageSize.Height - top) / pageSize.Height) * displaySize.Height;
        var displayWidth = ((right - left) / pageSize.Width) * displaySize.Width;
        var displayHeight = ((top - bottom) / pageSize.Height) * displaySize.Height;
        return new TextSelectionRect(displayLeft, displayTop, displayWidth, displayHeight);
    }

    private static List<ExtractedChar> ExtractCharacters(FpdfTextpageT textPage)
    {
        var charCount = fpdf_text.FPDFTextCountChars(textPage);
        var chars = new List<ExtractedChar>(Math.Max(0, charCount));

        for (var index = 0; index < charCount; index++)
        {
            var unicode = fpdf_text.FPDFTextGetUnicode(textPage, index);
            var text = TryConvertCodePoint(unicode);
            if (text is null || text is "\r" or "\n")
            {
                continue;
            }

            // Tight glyph box (used as a fallback for positioning).
            double tightLeft = 0;
            double tightRight = 0;
            double tightBottom = 0;
            double tightTop = 0;
            var hasTight = fpdf_text.FPDFTextGetCharBox(textPage, index, ref tightLeft, ref tightRight, ref tightBottom, ref tightTop) != 0;

            // Loose char box reflects text-matrix scaling, so it gives the visually rendered size
            // and a consistent top/bottom per baseline regardless of glyph shape.
            double left, right, bottom, top;
            var looseRect = new FS_RECTF_();
            if (fpdf_text.FPDFTextGetLooseCharBox(textPage, index, looseRect) != 0 &&
                looseRect.Right > looseRect.Left && looseRect.Top > looseRect.Bottom)
            {
                left = looseRect.Left;
                right = looseRect.Right;
                bottom = looseRect.Bottom;
                top = looseRect.Top;
            }
            else if (hasTight && tightRight > tightLeft && tightTop > tightBottom)
            {
                left = tightLeft;
                right = tightRight;
                bottom = tightBottom;
                top = tightTop;
            }
            else
            {
                continue;
            }

            var fontFlags = 0;
            var fontName = NormalizeFontName(GetFontName(textPage, index, ref fontFlags));
            var reportedFontSize = Convert.ToDouble(fpdf_text.FPDFTextGetFontSize(textPage, index));
            var fontWeight = fpdf_text.FPDFTextGetFontWeight(textPage, index);
            var fillColorHex = GetFillColorHex(textPage, index);
            var rotationDegrees = Convert.ToDouble(fpdf_text.FPDFTextGetCharAngle(textPage, index));
            var isItalic = (fontFlags & 64) != 0 ||
                           fontName.Contains("italic", StringComparison.OrdinalIgnoreCase) ||
                           fontName.Contains("oblique", StringComparison.OrdinalIgnoreCase);

            // FPDFText_GetFontSize returns the text-state font size directly, which is accurate for
            // most PDFs. It's only wrong when the PDF uses a text matrix to scale glyphs (LaTeX etc.),
            // in which case it's implausibly small. The loose char box height is in user-space points
            // but is roughly 1.15× the em size for typical fonts, so we use it only as a fallback and
            // normalize it down when we do.
            var boxHeight = top - bottom;
            var fontSize = reportedFontSize >= 3d
                ? reportedFontSize
                : boxHeight / 1.15d;

            chars.Add(new ExtractedChar(
                index,
                text,
                new PdfExtractionBounds(left, bottom, right, top),
                new PdfTextStyle(
                    fontName,
                    fontSize,
                    fontWeight,
                    isItalic,
                    fillColorHex,
                    rotationDegrees)));
        }

        // PDFium returns characters in content-stream order, which is often not reading order.
        // Re-sort into reading order: top-to-bottom, then left-to-right within the same line.
        // We bucket Y by half the median font size so jitter doesn't split a line across buckets.
        if (chars.Count > 1)
        {
            var medianSize = MedianFontSize(chars);
            var bucket = Math.Max(2d, medianSize * 0.6d);

            chars.Sort((a, b) =>
            {
                var bucketA = Math.Round(a.Bounds.Top / bucket);
                var bucketB = Math.Round(b.Bounds.Top / bucket);
                if (bucketA != bucketB)
                {
                    return bucketB.CompareTo(bucketA); // higher Y (top of page) first
                }

                var leftCompare = a.Bounds.Left.CompareTo(b.Bounds.Left);
                if (leftCompare != 0)
                {
                    return leftCompare;
                }

                return a.CharIndex.CompareTo(b.CharIndex);
            });
        }

        return chars;
    }

    private static double MedianFontSize(List<ExtractedChar> chars)
    {
        if (chars.Count == 0)
        {
            return 12d;
        }

        var sizes = new List<double>(chars.Count);
        foreach (var ch in chars)
        {
            if (ch.Style.FontSizePoints > 0)
            {
                sizes.Add(ch.Style.FontSizePoints);
            }
        }

        if (sizes.Count == 0)
        {
            return 12d;
        }

        sizes.Sort();
        return sizes[sizes.Count / 2];
    }

    private static List<PdfTextLine> BuildLines(List<ExtractedChar> chars)
    {
        var lines = new List<PdfTextLine>();
        var currentChars = new List<ExtractedChar>();
        ExtractedChar? previous = null;

        foreach (var current in chars)
        {
            if (previous is not null && StartsNewLine(previous, current))
            {
                AddLine(lines, currentChars);
                currentChars = [];
            }

            currentChars.Add(current);
            previous = current;
        }

        AddLine(lines, currentChars);
        return lines;
    }

    private static List<PdfTextBlock> BuildBlocks(List<PdfTextLine> lines)
    {
        var blocks = new List<PdfTextBlock>();
        var currentLines = new List<PdfTextLine>();
        PdfTextLine? previous = null;

        foreach (var line in lines)
        {
            if (previous is not null && StartsNewBlock(previous, line))
            {
                AddBlock(blocks, currentLines);
                currentLines = [];
            }

            currentLines.Add(line);
            previous = line;
        }

        AddBlock(blocks, currentLines);
        return blocks;
    }

    private static void AddLine(List<PdfTextLine> lines, List<ExtractedChar> lineChars)
    {
        if (lineChars.Count == 0)
        {
            return;
        }

        var runs = BuildRuns(lineChars);
        if (runs.Count == 0)
        {
            return;
        }

        lines.Add(new PdfTextLine(
            lines.Count,
            PdfExtractionBounds.Union(runs.Select(run => run.Bounds)),
            runs));
    }

    private static void AddBlock(List<PdfTextBlock> blocks, List<PdfTextLine> blockLines)
    {
        if (blockLines.Count == 0)
        {
            return;
        }

        blocks.Add(new PdfTextBlock(
            blocks.Count,
            PdfExtractionBounds.Union(blockLines.Select(line => line.Bounds)),
            blockLines.ToList()));
    }

    private static List<PdfTextRun> BuildRuns(List<ExtractedChar> lineChars)
    {
        var runs = new List<PdfTextRun>();
        var currentChars = new List<ExtractedChar>();
        ExtractedChar? previous = null;

        foreach (var current in lineChars)
        {
            if (previous is not null && !HasSameStyle(previous.Style, current.Style))
            {
                AddRun(runs, currentChars);
                currentChars = [];
            }

            currentChars.Add(current);
            previous = current;
        }

        AddRun(runs, currentChars);
        return runs;
    }

    private static void AddRun(List<PdfTextRun> runs, List<ExtractedChar> runChars)
    {
        if (runChars.Count == 0)
        {
            return;
        }

        runs.Add(new PdfTextRun(
            runs.Count,
            runChars[0].CharIndex,
            runChars.Count,
            string.Concat(runChars.Select(item => item.Text)),
            PdfExtractionBounds.Union(runChars.Select(item => item.Bounds)),
            runChars[0].Style));
    }

    private static bool StartsNewLine(ExtractedChar previous, ExtractedChar current)
    {
        // With loose char boxes the top (ascender) is consistent for every character on a baseline,
        // so we compare tops directly and use a tight tolerance.
        var verticalShift = Math.Abs(current.Bounds.Top - previous.Bounds.Top);
        var tolerance = Math.Max(1.5d, Math.Max(previous.Style.FontSizePoints, current.Style.FontSizePoints) * 0.35d);
        return verticalShift > tolerance;
    }

    private static bool StartsNewBlock(PdfTextLine previous, PdfTextLine current)
    {
        var previousFontSize = GetRepresentativeFontSize(previous);
        var currentFontSize = GetRepresentativeFontSize(current);
        var gap = previous.Bounds.Bottom - current.Bounds.Top;
        var indentDelta = Math.Abs(previous.Bounds.Left - current.Bounds.Left);
        var gapThreshold = Math.Max(6d, Math.Max(previousFontSize, currentFontSize) * 0.85d);
        var indentThreshold = Math.Max(18d, Math.Max(previousFontSize, currentFontSize) * 1.2d);

        return gap > gapThreshold * 1.35d || indentDelta > indentThreshold;
    }

    private static double GetRepresentativeFontSize(PdfTextLine line)
    {
        return line.Runs.Count == 0 ? 12d : line.Runs.Max(run => run.Style.FontSizePoints);
    }

    private static bool HasSameStyle(PdfTextStyle left, PdfTextStyle right)
    {
        return string.Equals(left.FontName, right.FontName, StringComparison.Ordinal) &&
               Math.Abs(left.FontSizePoints - right.FontSizePoints) <= 0.25d &&
               Math.Abs(left.RotationDegrees - right.RotationDegrees) <= 0.5d &&
               left.FontWeight == right.FontWeight &&
               left.IsItalic == right.IsItalic &&
               string.Equals(left.FillColorHex, right.FillColorHex, StringComparison.Ordinal);
    }

    private static string GetFontName(FpdfTextpageT textPage, int index, ref int flags)
    {
        var length = fpdf_text.FPDFTextGetFontInfo(textPage, index, IntPtr.Zero, 0, ref flags);
        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            var written = fpdf_text.FPDFTextGetFontInfo(textPage, index, buffer, length, ref flags);
            if (written == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[Math.Max(0, (int)written - 1)];
            if (bytes.Length > 0)
            {
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
            }

            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string NormalizeFontName(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return "Unknown";
        }

        if (fontName.Length > 7 &&
            fontName[6] == '+' &&
            fontName.Take(6).All(static ch => ch is >= 'A' and <= 'Z'))
        {
            return fontName[7..];
        }

        return fontName;
    }

    private static string GetFillColorHex(FpdfTextpageT textPage, int index)
    {
        uint r = 0;
        uint g = 0;
        uint b = 0;
        uint a = 255;

        if (fpdf_text.FPDFTextGetFillColor(textPage, index, ref r, ref g, ref b, ref a) == 0)
        {
            return "#000000";
        }

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static string? TryConvertCodePoint(uint unicode)
    {
        // Drop anything PDFium could not map or that XML 1.0 can't serialize.
        if (unicode == 0 || unicode > 0x10FFFF)
        {
            return null;
        }

        // Lone surrogates are not valid Unicode scalar values and would throw in ConvertFromUtf32.
        if (unicode >= 0xD800 && unicode <= 0xDFFF)
        {
            return null;
        }

        // Non-characters that are illegal in XML 1.0.
        if (unicode == 0xFFFE || unicode == 0xFFFF)
        {
            return null;
        }

        // Control characters that are illegal in XML 1.0 (only \t, \n, \r are allowed below 0x20).
        if (unicode < 0x20 && unicode != 0x09 && unicode != 0x0A && unicode != 0x0D)
        {
            return null;
        }

        // C1 control block (0x7F-0x9F) — not illegal in XML but tends to be garbage font glyphs from PDFs.
        if (unicode >= 0x7F && unicode <= 0x9F)
        {
            return null;
        }

        try
        {
            return char.ConvertFromUtf32((int)unicode);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private sealed record ExtractedChar(
        int CharIndex,
        string Text,
        PdfExtractionBounds Bounds,
        PdfTextStyle Style);

    private static void EnsureLibrary()
    {
        lock (LibraryLock)
        {
            if (_libraryUsers == 0)
            {
                fpdfview.FPDF_InitLibrary();
            }

            _libraryUsers++;
        }
    }

    private static void ReleaseLibrary()
    {
        lock (LibraryLock)
        {
            _libraryUsers--;
            if (_libraryUsers == 0)
            {
                fpdfview.FPDF_DestroyLibrary();
            }
        }
    }
}

public sealed class PdfOutlineNode
{
    public PdfOutlineNode(string title, int pageNumber)
    {
        Title = title;
        PageNumber = pageNumber;
    }

    public string Title { get; }

    public int PageNumber { get; }

    public Collection<PdfOutlineNode> Children { get; } = [];
}

public sealed class PdfTextSelectionResult
{
    public PdfTextSelectionResult(string text, IReadOnlyList<TextSelectionRect> rects)
    {
        Text = text;
        Rects = rects;
    }

    public string Text { get; }

    public IReadOnlyList<TextSelectionRect> Rects { get; }
}
