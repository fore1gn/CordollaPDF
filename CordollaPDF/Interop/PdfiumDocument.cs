using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;
using CordollaPDF.Models;

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

    public PdfLinkTarget? GetLinkTargetAt(int pageIndex, Point displayPoint, Size displaySize)
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
                var pageSize = PageSizes[pageIndex];
                var pdfPoint = DisplayToPdfPoint(displayPoint, pageSize, displaySize);
                var link = fpdf_doc.FPDFLinkGetLinkAtPoint(page, pdfPoint.X, pdfPoint.Y);
                return link == default ? null : ResolveLinkTarget(link);
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

    private PdfLinkTarget? ResolveLinkTarget(FpdfLinkT link)
    {
        var dest = fpdf_doc.FPDFLinkGetDest(Handle, link);
        var pageTarget = ResolveDestinationTarget(dest);
        if (pageTarget is not null)
        {
            return pageTarget;
        }

        var action = fpdf_doc.FPDFLinkGetAction(link);
        return action == default ? null : ResolveActionTarget(action);
    }

    private PdfLinkTarget? ResolveActionTarget(FpdfActionT action)
    {
        const int pdfActionGoTo = 1;
        const int pdfActionRemoteGoTo = 2;
        const int pdfActionUri = 3;

        var actionType = fpdf_doc.FPDFActionGetType(action);
        if (actionType is pdfActionGoTo or pdfActionRemoteGoTo)
        {
            return ResolveDestinationTarget(fpdf_doc.FPDFActionGetDest(Handle, action));
        }

        if (actionType == pdfActionUri)
        {
            var uri = ReadActionUri(action);
            return string.IsNullOrWhiteSpace(uri) ? null : PdfLinkTarget.ForUri(uri);
        }

        return null;
    }

    private PdfLinkTarget? ResolveDestinationTarget(FpdfDestT dest)
    {
        if (dest == default)
        {
            return null;
        }

        var pageIndex = fpdf_doc.FPDFDestGetDestPageIndex(Handle, dest);
        return pageIndex >= 0 ? PdfLinkTarget.ForPage(pageIndex + 1) : null;
    }

    private string? ReadActionUri(FpdfActionT action)
    {
        var byteCount = fpdf_doc.FPDFActionGetURIPath(Handle, action, IntPtr.Zero, 0);
        if (byteCount <= 1)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)byteCount);
        try
        {
            var written = fpdf_doc.FPDFActionGetURIPath(Handle, action, buffer, byteCount);
            if (written <= 1)
            {
                return null;
            }

            var bytes = new byte[written - 1];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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

public sealed class PdfLinkTarget
{
    private PdfLinkTarget(PdfLinkTargetKind kind, int pageNumber, string? uri)
    {
        Kind = kind;
        PageNumber = pageNumber;
        Uri = uri;
    }

    public PdfLinkTargetKind Kind { get; }

    public int PageNumber { get; }

    public string? Uri { get; }

    public static PdfLinkTarget ForPage(int pageNumber) => new(PdfLinkTargetKind.Page, pageNumber, null);

    public static PdfLinkTarget ForUri(string uri) => new(PdfLinkTargetKind.Uri, 0, uri);
}

public enum PdfLinkTargetKind
{
    Page,
    Uri
}
