namespace CordollaPDF.Models;

public sealed class TextSelectionRect
{
    public TextSelectionRect(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }
}
