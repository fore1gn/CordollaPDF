using CordollaPDF.Behaviors;
using CordollaPDF.Interop;
using CordollaPDF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CordollaPDF;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const double HorizontalPadding = 72;
    private const double VerticalPadding = 44;
    private const double PageGap = 30;
    private const double SpreadGap = 42;
    private const double MinZoom = 0.35;
    private const double MaxZoom = 3.0;
    private const double KeyboardScrollDelta = 133;
    private static readonly TimeSpan DoubleGThreshold = TimeSpan.FromMilliseconds(600);

    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly DispatcherTimer _renderDebounceTimer;
    private readonly DispatcherTimer _dKeyScrollTimer;
    private readonly DispatcherTimer _uKeyScrollTimer;
    private readonly AppStateStore _appStateStore = new();

    private PdfiumDocument? _document;
    private string _documentName = "Drop a PDF or choose File > Open";
    private string _sidebarDocumentLabel = "No document loaded";
    private string _statusText = "Ready";
    private string _windowCaption = "CordollaPDF";
    private int _currentPage = 1;
    private int _totalPages;
    private bool _hasDocument;
    private bool _isTwoPageMode = true;
    private bool _isFitToScreen = true;
    private bool _isSidebarCollapsed;
    private double _manualZoom = 1.1;
    private double _activeZoom = 1.1;
    private string? _currentPath;
    private long _documentVersion;
    private bool _isDKeyScrolling;
    private bool _isUKeyScrolling;
    private DateTime _lastGKeyPressUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _renderDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _renderDebounceTimer.Tick += (_, _) =>
        {
            _renderDebounceTimer.Stop();
            RecalculateLayout();
            QueueVisibleRenders();
        };

        _dKeyScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _dKeyScrollTimer.Tick += (_, _) => ScrollDownWithDKey();

        _uKeyScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _uKeyScrollTimer.Tick += (_, _) => ScrollUpWithUKey();

        Loaded += OnLoaded;
        StateChanged += (_, _) =>
        {
            UpdateWindowCornerClip();
            PersistAppState();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<OutlineItem> OutlineItems { get; } = [];

    public ObservableCollection<SpreadViewModel> Spreads { get; } = [];

    public string DocumentName
    {
        get => _documentName;
        private set => SetField(ref _documentName, value);
    }

    public string SidebarDocumentLabel
    {
        get => _sidebarDocumentLabel;
        private set => SetField(ref _sidebarDocumentLabel, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string WindowCaption
    {
        get => _windowCaption;
        private set => SetField(ref _windowCaption, value);
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var normalized = Math.Clamp(value, 1, Math.Max(1, _totalPages));
            if (SetField(ref _currentPage, normalized))
            {
                OnPropertyChanged(nameof(PageCountText));
            }
        }
    }

    public bool HasDocument
    {
        get => _hasDocument;
        private set => SetField(ref _hasDocument, value);
    }

    public GridLength SidebarColumnWidth => _isSidebarCollapsed ? new GridLength(0) : new GridLength(390);

    public Visibility SidebarVisibility => _isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

    public string TableOfContentsMenuText => _isSidebarCollapsed ? "Show Table Of Contents" : "Collapse Table Of Contents";

    public string PageCountText => $"/ {Math.Max(1, _totalPages)}";

    public string ZoomText => $"{Math.Round(_activeZoom * 100):0}%";

    public string PageModeButtonText => _isTwoPageMode ? "Two Pages" : "One Page";

    public string TitleBarDocumentName => HasDocument ? DocumentName : string.Empty;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RestoreAppState();
        UpdateWindowCornerClip();
        await TryLoadFromArgumentsAsync();
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowCornerClip();
    }

    private async void OpenMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await OpenDocumentPickerAsync();
    }

    private async Task OpenDocumentPickerAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open PDF",
            Filter = "PDF Files (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadDocumentAsync(dialog.FileName);
        }
    }

    private void CloseMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        CloseDocument();
    }

    private void JumpToTopMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ScrollToTop();
    }

    private void JumpToBottomMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ScrollToBottom();
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseCaptionButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://google.com") { UseShellExecute = true });
    }

    private void TogglePageModeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        TogglePageMode();
    }

    private void TogglePageModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        TogglePageMode();
    }

    private void ToggleTableOfContentsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleTableOfContents();
    }

    private void FitButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleFitToScreen();
    }

    private void ZoomInButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustZoom(1.12);
    }

    private void ZoomOutButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustZoom(1 / 1.12);
    }

    private void GoToPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        ScrollToPage(CurrentPage);
    }

    private void PageNumberTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitPageNumberNavigation();
        e.Handled = true;
    }

    private void PageNumberTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitPageNumberNavigation();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputFocused())
        {
            return;
        }

        if (e.Key == Key.D)
        {
            if (!_isDKeyScrolling)
            {
                _isDKeyScrolling = true;
                ScrollDownWithDKey();
                _dKeyScrollTimer.Start();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.U)
        {
            if (!_isUKeyScrolling)
            {
                _isUKeyScrolling = true;
                ScrollUpWithUKey();
                _uKeyScrollTimer.Start();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ScrollToBottom();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G)
        {
            HandleGKeyPress();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.H)
        {
            ToggleTableOfContents();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F)
        {
            ToggleFitToScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = OpenDocumentPickerAsync();
            e.Handled = true;
        }
    }

    private void Window_OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.D)
        {
            StopDKeyScrolling();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.U)
        {
            StopUKeyScrolling();
            e.Handled = true;
        }
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        StopDKeyScrolling();
        StopUKeyScrolling();
    }

    private void OutlineTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineItem item)
        {
            ScrollToPage(item.PageNumber);
        }
    }

    private void ViewerScrollViewer_OnScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (!HasDocument)
        {
            return;
        }

        UpdateCurrentPageFromScrollPosition();
        RestartRenderDebounce();
    }

    private void ViewerScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HasDocument)
        {
            RestartRenderDebounce();
        }
    }

    private async void ViewerScrollViewer_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var path = files.FirstOrDefault(file => string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase));
        if (path is not null)
        {
            await LoadDocumentAsync(path);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        PersistAppState();
        base.OnClosed(e);
        _document?.Dispose();
        _renderGate.Dispose();
    }

    private async Task LoadDocumentAsync(string path)
    {
        try
        {
            StatusText = "Loading PDF...";
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            var document = await Task.Run(() => new PdfiumDocument(path));
            var documentVersion = Interlocked.Increment(ref _documentVersion);

            _document?.Dispose();
            _document = document;
            _currentPath = path;
            _totalPages = document.PageCount;
            HasDocument = true;

            DocumentName = document.Name;
            SidebarDocumentLabel = document.Name;
            WindowCaption = $"CordollaPDF - {document.Name}";
            CurrentPage = 1;
            OnPropertyChanged(nameof(TitleBarDocumentName));

            BuildOutline(document);
            BuildSpreads(document);
            RecalculateLayout();
            SmoothScrollBehavior.AnimateTo(ViewerScrollViewer, 0, 120);
            QueueVisibleRenders(documentVersion);

            StatusText = $"Loaded {_totalPages} pages";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"The PDF could not be opened.\n\n{ex.Message}",
                "Open Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            StatusText = "Open failed";
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private void CloseDocument()
    {
        _document?.Dispose();
        _document = null;
        Interlocked.Increment(ref _documentVersion);
        _currentPath = null;
        _totalPages = 0;
        HasDocument = false;

        Spreads.Clear();
        OutlineItems.Clear();

        CurrentPage = 1;
        DocumentName = "Drop a PDF or choose File > Open";
        SidebarDocumentLabel = "No document loaded";
        WindowCaption = "CordollaPDF";
        StatusText = "Ready";
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(TitleBarDocumentName));
    }

    private void BuildSpreads(PdfiumDocument document)
    {
        Spreads.Clear();

        var index = 0;
        while (index < document.PageSizes.Count)
        {
            var leftSize = document.PageSizes[index];
            var left = new PageSlotViewModel(index + 1, leftSize.Width, leftSize.Height);

            PageSlotViewModel? right = null;
            if (_isTwoPageMode && index + 1 < document.PageSizes.Count)
            {
                var rightSize = document.PageSizes[index + 1];
                right = new PageSlotViewModel(index + 2, rightSize.Width, rightSize.Height);
            }

            Spreads.Add(new SpreadViewModel(left, right));
            index += _isTwoPageMode ? 2 : 1;
        }

        OnPropertyChanged(nameof(PageModeButtonText));
    }

    private void BuildOutline(PdfiumDocument document)
    {
        OutlineItems.Clear();

        foreach (var item in document.Bookmarks)
        {
            OutlineItems.Add(ConvertOutline(item));
        }

        if (OutlineItems.Count > 0)
        {
            return;
        }

        for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
        {
            OutlineItems.Add(new OutlineItem($"Page {pageNumber}", pageNumber));
        }
    }

    private static OutlineItem ConvertOutline(PdfOutlineNode node)
    {
        var item = new OutlineItem(node.Title, node.PageNumber);
        foreach (var child in node.Children)
        {
            item.Children.Add(ConvertOutline(child));
        }

        return item;
    }

    private void TogglePageMode()
    {
        _isTwoPageMode = !_isTwoPageMode;
        StatusText = _isTwoPageMode ? "Two-page mode enabled" : "Single-page mode enabled";

        if (_document is null)
        {
            OnPropertyChanged(nameof(PageModeButtonText));
            return;
        }

        BuildSpreads(_document);
        RecalculateLayout();
        ScrollToPage(CurrentPage);
        QueueVisibleRenders();
    }

    private void AdjustZoom(double factor)
    {
        _manualZoom = Math.Clamp(_activeZoom * factor, MinZoom, MaxZoom);
        _isFitToScreen = false;
        StatusText = $"Zoom set to {Math.Round(_manualZoom * 100):0}%";
        RecalculateLayout();
        QueueVisibleRenders();
    }

    private void RecalculateLayout()
    {
        if (_document is null || !HasDocument || ViewerScrollViewer.ViewportWidth <= 0 || ViewerScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(400, ViewerScrollViewer.ViewportWidth - HorizontalPadding);
        var availableHeight = Math.Max(300, ViewerScrollViewer.ViewportHeight - VerticalPadding);

        var referenceSpread = Spreads.FirstOrDefault(spread => spread.FirstPageNumber <= CurrentPage && CurrentPage <= spread.LastPageNumber)
            ?? Spreads.FirstOrDefault();

        if (referenceSpread is null)
        {
            return;
        }

        var widthUnits = referenceSpread.LeftPage.SourceWidth + (referenceSpread.RightPage?.SourceWidth ?? 0);
        if (referenceSpread.RightPage is not null)
        {
            widthUnits += PageGap;
        }

        var heightUnits = Math.Max(referenceSpread.LeftPage.SourceHeight, referenceSpread.RightPage?.SourceHeight ?? 0);
        var fitScale = Math.Min(availableWidth / Math.Max(1, widthUnits), availableHeight / Math.Max(1, heightUnits));

        _activeZoom = _isFitToScreen ? fitScale : _manualZoom;

        foreach (var spread in Spreads)
        {
            spread.LeftPage.DisplayWidth = spread.LeftPage.SourceWidth * _activeZoom;
            spread.LeftPage.DisplayHeight = spread.LeftPage.SourceHeight * _activeZoom;

            if (spread.RightPage is not null)
            {
                spread.RightPage.DisplayWidth = spread.RightPage.SourceWidth * _activeZoom;
                spread.RightPage.DisplayHeight = spread.RightPage.SourceHeight * _activeZoom;
            }

            spread.TotalHeight = Math.Max(spread.LeftPage.DisplayHeight, spread.RightPage?.DisplayHeight ?? 0) + SpreadGap;
        }

        OnPropertyChanged(nameof(ZoomText));
    }

    private void QueueVisibleRenders(long? forcedVersion = null)
    {
        if (_document is null || !HasDocument || ViewerScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var activeVersion = forcedVersion ?? Volatile.Read(ref _documentVersion);

        var top = Math.Max(0, ViewerScrollViewer.VerticalOffset - ViewerScrollViewer.ViewportHeight * 1.2);
        var bottom = ViewerScrollViewer.VerticalOffset + ViewerScrollViewer.ViewportHeight * 2.2;
        double cursor = 0;

        foreach (var spread in Spreads)
        {
            var spreadTop = cursor;
            var spreadBottom = cursor + spread.TotalHeight;
            var isNearViewport = spreadBottom >= top && spreadTop <= bottom;

            QueuePageRenderIfNeeded(spread.LeftPage, isNearViewport, activeVersion);
            if (spread.RightPage is not null)
            {
                QueuePageRenderIfNeeded(spread.RightPage, isNearViewport, activeVersion);
            }

            if (!isNearViewport && spreadBottom < ViewerScrollViewer.VerticalOffset - ViewerScrollViewer.ViewportHeight * 3)
            {
                spread.LeftPage.ClearImage();
                spread.RightPage?.ClearImage();
            }

            cursor += spread.TotalHeight;
        }
    }

    private void QueuePageRenderIfNeeded(PageSlotViewModel page, bool shouldRender, long documentVersion)
    {
        if (!shouldRender || _document is null)
        {
            return;
        }

        var desiredPixelWidth = Math.Max(300, (int)Math.Ceiling(page.DisplayWidth));
        if (page.Image is not null && Math.Abs(page.LastRenderedPixelWidth - desiredPixelWidth) <= 36)
        {
            return;
        }

        if (page.IsRendering)
        {
            return;
        }

        var token = page.RenderToken + 1;
        page.RenderToken = token;
        page.IsRendering = true;

        _ = RenderPageAsync(_document, page, token, desiredPixelWidth, documentVersion);
    }

    private async Task RenderPageAsync(PdfiumDocument document, PageSlotViewModel page, int token, int desiredPixelWidth, long documentVersion)
    {
        if (_document is null)
        {
            return;
        }

        await _renderGate.WaitAsync();

        try
        {
            if (!ReferenceEquals(_document, document) || Volatile.Read(ref _documentVersion) != documentVersion)
            {
                await Dispatcher.InvokeAsync(() => page.IsRendering = false);
                return;
            }

            var desiredPixelHeight = Math.Max(300, (int)Math.Ceiling(page.DisplayHeight));
            var bitmapSource = await Task.Run(() => document.RenderPage(page.PageNumber - 1, desiredPixelWidth, desiredPixelHeight));

            await Dispatcher.InvokeAsync(() =>
            {
                if (page.RenderToken != token || !ReferenceEquals(_document, document) || Volatile.Read(ref _documentVersion) != documentVersion)
                {
                    page.IsRendering = false;
                    return;
                }

                page.Image = bitmapSource;
                page.LastRenderedPixelWidth = desiredPixelWidth;
                page.IsRendering = false;
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() => page.IsRendering = false);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private void ScrollToPage(int pageNumber)
    {
        if (!HasDocument)
        {
            return;
        }

        var page = Math.Clamp(pageNumber, 1, Math.Max(1, _totalPages));
        CurrentPage = page;

        double offset = 0;
        foreach (var spread in Spreads)
        {
            if (page >= spread.FirstPageNumber && page <= spread.LastPageNumber)
            {
                SmoothScrollBehavior.AnimateTo(ViewerScrollViewer, Math.Max(0, offset - 12), 180);
                QueueVisibleRenders();
                return;
            }

            offset += spread.TotalHeight;
        }
    }

    private void UpdateCurrentPageFromScrollPosition()
    {
        if (!HasDocument)
        {
            return;
        }

        var center = ViewerScrollViewer.VerticalOffset + (ViewerScrollViewer.ViewportHeight / 2);
        double cursor = 0;

        foreach (var spread in Spreads)
        {
            var next = cursor + spread.TotalHeight;
            if (center <= next)
            {
                CurrentPage = spread.FirstPageNumber;
                return;
            }

            cursor = next;
        }

        CurrentPage = Math.Max(1, _totalPages);
    }

    private void RestartRenderDebounce()
    {
        _renderDebounceTimer.Stop();
        _renderDebounceTimer.Start();
    }

    private async Task TryLoadFromArgumentsAsync()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var existing = args.FirstOrDefault(arg => File.Exists(arg) && string.Equals(Path.GetExtension(arg), ".pdf", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            await LoadDocumentAsync(existing);
            return;
        }

        var samplePath = Path.Combine(AppContext.BaseDirectory, "sample.pdf");
        if (_currentPath is null && File.Exists(samplePath))
        {
            await LoadDocumentAsync(samplePath);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void RestoreAppState()
    {
        var state = _appStateStore.Load();
        _isSidebarCollapsed = state.IsSidebarCollapsed;
        NotifySidebarStateChanged();

        if (state.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void NotifySidebarStateChanged()
    {
        OnPropertyChanged(nameof(SidebarColumnWidth));
        OnPropertyChanged(nameof(SidebarVisibility));
        OnPropertyChanged(nameof(TableOfContentsMenuText));
    }

    private void PersistAppState()
    {
        _appStateStore.Save(new AppState
        {
            IsSidebarCollapsed = _isSidebarCollapsed,
            IsMaximized = WindowState == WindowState.Maximized
        });
    }

    private void ToggleTableOfContents()
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        StatusText = _isSidebarCollapsed ? "Table of contents collapsed" : "Table of contents shown";
        NotifySidebarStateChanged();
        PersistAppState();

        Dispatcher.InvokeAsync(() =>
        {
            RecalculateLayout();
            QueueVisibleRenders();
        }, DispatcherPriority.Background);
    }

    private void ToggleFitToScreen()
    {
        _isFitToScreen = !_isFitToScreen;
        StatusText = _isFitToScreen ? "Fit-to-screen enabled" : "Fit-to-screen disabled";
        RecalculateLayout();
        QueueVisibleRenders();
    }

    private void CommitPageNumberNavigation()
    {
        if (!HasDocument)
        {
            return;
        }

        ScrollToPage(CurrentPage);
    }

    private void ScrollDownWithDKey()
    {
        if (!HasDocument)
        {
            StopDKeyScrolling();
            return;
        }

        SmoothScrollBehavior.ScrollBy(ViewerScrollViewer, KeyboardScrollDelta, 110);
    }

    private void ScrollUpWithUKey()
    {
        if (!HasDocument)
        {
            StopUKeyScrolling();
            return;
        }

        SmoothScrollBehavior.ScrollBy(ViewerScrollViewer, -KeyboardScrollDelta, 110);
    }

    private void StopDKeyScrolling()
    {
        _isDKeyScrolling = false;
        _dKeyScrollTimer.Stop();
    }

    private void StopUKeyScrolling()
    {
        _isUKeyScrolling = false;
        _uKeyScrollTimer.Stop();
    }

    private void HandleGKeyPress()
    {
        if (!HasDocument)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastGKeyPressUtc <= DoubleGThreshold)
        {
            _lastGKeyPressUtc = DateTime.MinValue;
            ScrollToTop();
            return;
        }

        _lastGKeyPressUtc = now;
    }

    private void ScrollToTop()
    {
        if (!HasDocument)
        {
            return;
        }

        CurrentPage = 1;
        SmoothScrollBehavior.AnimateTo(ViewerScrollViewer, 0, 180);
        QueueVisibleRenders();
        StatusText = "Jumped to top";
    }

    private void ScrollToBottom()
    {
        if (!HasDocument)
        {
            return;
        }

        CurrentPage = Math.Max(1, _totalPages);
        var bottomOffset = Math.Max(0, ViewerScrollViewer.ExtentHeight - ViewerScrollViewer.ViewportHeight);
        SmoothScrollBehavior.AnimateTo(ViewerScrollViewer, bottomOffset, 220);
        QueueVisibleRenders();
        StatusText = "Jumped to bottom";
    }

    private bool IsTextInputFocused()
    {
        return FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox;
    }

    private void UpdateWindowCornerClip()
    {
        if (WindowContentBorder is null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowContentBorder.Clip = null;
            return;
        }

        var width = Math.Max(0, WindowContentBorder.ActualWidth);
        var height = Math.Max(0, WindowContentBorder.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        WindowContentBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, width, height),
            9,
            9);
    }
}
