using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Sonulab.App.Behaviors;

// Draws a 2px insertion line at the computed drop position.
// Uses a Canvas overlay placed over the ListBox inside the view — avoids AdornerLayer positioning
// quirks in Avalonia 12.  The Canvas is created lazily and inserted into the ListBox's parent Panel.
public static class PresetDropIndicator
{
    private static Canvas? _overlay;
    private static Rectangle? _line;

    public static void Show(ListBox list, int index)
    {
        // Lazily create the overlay canvas parented to the same panel as the list.
        if (_overlay is null)
        {
            _overlay = new Canvas
            {
                IsHitTestVisible = false,
                ZIndex = 1000
            };
            _line = new Rectangle
            {
                Height = 2,
                Fill = Brushes.DodgerBlue,
                IsHitTestVisible = false
            };
            _overlay.Children.Add(_line);
        }

        // Attach to parent panel if not already.
        if (_overlay.Parent is null && list.Parent is Panel panel)
            panel.Children.Add(_overlay);

        // Compute Y position by finding the row at `index`.
        double y = 0;
        if (index <= 0)
        {
            if (list.ContainerFromIndex(0) is Control first)
                y = list.Bounds.Y + first.Bounds.Y;
            else
                y = list.Bounds.Y;
        }
        else if (list.ContainerFromIndex(index) is Control at)
        {
            y = list.Bounds.Y + at.Bounds.Y;
        }
        else if (list.ContainerFromIndex(index - 1) is Control prev)
        {
            y = list.Bounds.Y + prev.Bounds.Y + prev.Bounds.Height;
        }
        else
        {
            y = list.Bounds.Y + list.Bounds.Height;
        }

        if (_line is not null)
        {
            _line.Width = list.Bounds.Width;
            Canvas.SetLeft(_line, list.Bounds.X);
            Canvas.SetTop(_line, y);
        }
    }

    public static void Hide()
    {
        if (_overlay?.Parent is Panel p)
            p.Children.Remove(_overlay);
        _overlay = null;
        _line = null;
    }
}
