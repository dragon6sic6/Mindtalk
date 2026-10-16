using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Mindtalk.App.Controls;

/// <summary>Lays its children out in rows, wrapping when a row is full (the Mac's FlowLayout).</summary>
public sealed partial class FlowPanel : Panel
{
    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            x += size.Width + Spacing;
            widest = Math.Max(widest, x - Spacing);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(widest, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, rowHeight = 0;
        var row = new List<UIElement>();
        void Place()
        {
            var left = 0.0;
            foreach (var child in row)
            {
                var size = child.DesiredSize;
                child.Arrange(new Rect(left, y + (rowHeight - size.Height) / 2, size.Width, size.Height));
                left += size.Width + Spacing;
            }
            row.Clear();
        }
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width)
            {
                Place();
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            row.Add(child);
            x += size.Width + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        Place();
        return final;
    }
}
