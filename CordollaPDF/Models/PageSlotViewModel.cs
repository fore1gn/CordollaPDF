using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public PageSlotViewModel(int pageNumber, double sourceWidth, double sourceHeight)
    {
        PageNumber = pageNumber;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public void ClearImage()
    {
        Image = null;
        IsRendering = false;
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
