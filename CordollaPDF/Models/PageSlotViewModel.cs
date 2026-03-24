using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace CordollaPDF.Models;

public sealed class PageSlotViewModel : INotifyPropertyChanged
{
    private double _displayWidth;
    private double _displayHeight;
    private ImageSource? _image;
    private bool _isRendering;
    private int _renderToken;
    private int _lastRenderedPixelWidth;
    private string _selectedText = string.Empty;

    public PageSlotViewModel(int pageNumber, double sourceWidth, double sourceHeight)
    {
        PageNumber = pageNumber;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TextSelectionRect> SelectionRects { get; } = [];

    public ObservableCollection<TextSelectionRect> SearchRects { get; } = [];

    public int PageNumber { get; }

    public double SourceWidth { get; }

    public double SourceHeight { get; }

    public double DisplayWidth
    {
        get => _displayWidth;
        set => SetField(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        set => SetField(ref _displayHeight, value);
    }

    public ImageSource? Image
    {
        get => _image;
        set => SetField(ref _image, value);
    }

    public bool IsRendering
    {
        get => _isRendering;
        set => SetField(ref _isRendering, value);
    }

    public int RenderToken
    {
        get => _renderToken;
        set => SetField(ref _renderToken, value);
    }

    public int LastRenderedPixelWidth
    {
        get => _lastRenderedPixelWidth;
        set => SetField(ref _lastRenderedPixelWidth, value);
    }

    public bool HasSelection => SelectionRects.Count > 0;

    public string SelectedText
    {
        get => _selectedText;
        private set => SetField(ref _selectedText, value);
    }

    public void ClearImage()
    {
        Image = null;
        IsRendering = false;
    }

    public void SetSelection(IReadOnlyList<TextSelectionRect> rects, string text)
    {
        SelectionRects.Clear();
        foreach (var rect in rects)
        {
            SelectionRects.Add(rect);
        }

        SelectedText = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
    }

    public void ClearSelection()
    {
        if (SelectionRects.Count == 0 && string.IsNullOrEmpty(SelectedText))
        {
            return;
        }

        SelectionRects.Clear();
        SelectedText = string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
    }

    public void SetSearchHighlight(IReadOnlyList<TextSelectionRect> rects)
    {
        SearchRects.Clear();
        foreach (var rect in rects)
        {
            SearchRects.Add(rect);
        }
    }

    public void ClearSearchHighlight()
    {
        if (SearchRects.Count == 0)
        {
            return;
        }

        SearchRects.Clear();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
