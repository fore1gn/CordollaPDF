using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;

namespace CordollaPDF.Interop;

public sealed class PdfiumDocument : IDisposable
{
    private static readonly object LibraryLock = new();
    private static int _libraryUsers;

    private readonly object _syncRoot = new();
    private readonly byte[] _documentBytes;
    private readonly GCHandle _documentHandle;
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
