using System.Collections.ObjectModel;

namespace CordollaPDF.Models;

public sealed class OutlineItem
{
    public OutlineItem(string title, int pageNumber)
    {
        Title = title;
        PageNumber = pageNumber;
    }

    public string Title { get; }

    public int PageNumber { get; }

    public ObservableCollection<OutlineItem> Children { get; } = [];
}
