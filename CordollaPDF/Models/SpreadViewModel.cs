using System.ComponentModel;

namespace CordollaPDF.Models;

public sealed class SpreadViewModel : INotifyPropertyChanged
{
    private double _totalHeight;

    public SpreadViewModel(PageSlotViewModel leftPage, PageSlotViewModel? rightPage)
    {
        LeftPage = leftPage;
        RightPage = rightPage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PageSlotViewModel LeftPage { get; }

    public PageSlotViewModel? RightPage { get; }

    public bool HasRightPage => RightPage is not null;

    public double TotalHeight
    {
        get => _totalHeight;
        set
        {
            if (Math.Abs(_totalHeight - value) < 0.1)
            {
                return;
            }

            _totalHeight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalHeight)));
        }
    }

    public int FirstPageNumber => LeftPage.PageNumber;

    public int LastPageNumber => RightPage?.PageNumber ?? LeftPage.PageNumber;
}
