using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Mindtalk.App.Controls;

/// <summary>
/// SwiftUI's .shadow(color:radius:y:) for a rounded surface: a blurred copy of its shape,
/// drawn behind it. (A composition drop shadow masked by the shape, with room around it,
/// since a visual clips its shadow to its own size.)
/// </summary>
public static class SoftShadow
{
    private const float Room = 48;

    /// Wraps <paramref name="surface"/> in a grid that draws its shadow underneath.
    public static Grid Around(FrameworkElement surface, double cornerRadius, double radius, double y, double opacity)
    {
        var host = new Grid { IsHitTestVisible = false };
        var grid = new Grid
        {
            HorizontalAlignment = surface.HorizontalAlignment,
            VerticalAlignment = surface.VerticalAlignment,
            Margin = surface.Margin,
        };
        surface.Margin = new Thickness(0);
        grid.Children.Add(host);
        grid.Children.Add(surface);
        Attach(host, cornerRadius, radius, y, opacity);
        return grid;
    }

    /// Draws the shadow of a rounded rectangle the size of <paramref name="host"/>.
    public static void Attach(FrameworkElement host, double cornerRadius, double radius, double y, double opacity)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2((float)cornerRadius);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = compositor.CreateColorBrush(Colors.Black);
        var shapes = compositor.CreateShapeVisual();
        shapes.Shapes.Add(shape);
        shapes.Offset = new Vector3(Room, Room, 0);
        var holder = compositor.CreateContainerVisual();
        holder.Children.InsertAtTop(shapes);

        var surface = compositor.CreateVisualSurface();
        surface.SourceVisual = holder;
        var mask = compositor.CreateSurfaceBrush(surface);
        mask.Stretch = CompositionStretch.None;
        mask.HorizontalAlignmentRatio = 0;
        mask.VerticalAlignmentRatio = 0;

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = (float)(radius * 2);
        shadow.Offset = new Vector3(0, (float)y, 0);
        shadow.Color = Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0);
        shadow.Mask = mask;
        var sprite = compositor.CreateSpriteVisual();
        sprite.Offset = new Vector3(-Room, -Room, 0);
        sprite.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);

        void Resize()
        {
            var size = new Vector2((float)host.ActualWidth, (float)host.ActualHeight);
            geometry.Size = size;
            shapes.Size = size;
            var outer = size + new Vector2(2 * Room);
            sprite.Size = outer;
            surface.SourceSize = outer;
        }
        host.SizeChanged += (_, _) => Resize();
        Resize();
    }
}
