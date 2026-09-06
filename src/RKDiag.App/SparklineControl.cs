using System.Windows;
using System.Windows.Media;

namespace RKDiag.App;

public sealed class SparklineControl : FrameworkElement
{
    private readonly Queue<double> _values = new();
    private readonly Brush _lineBrush = new SolidColorBrush(Color.FromRgb(3, 129, 254));
    private readonly Brush _fillBrush = new LinearGradientBrush(
        Color.FromArgb(68, 3, 129, 254), Color.FromArgb(0, 3, 129, 254), 90);

    public void AddValue(double value)
    {
        _values.Enqueue(Math.Clamp(value, 0, 100));
        while (_values.Count > 72)
            _values.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(235, 238, 242)), 1);
        for (var row = 1; row < 4; row++)
        {
            var y = ActualHeight * row / 4;
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        }

        if (_values.Count < 2)
            return;

        var values = _values.ToArray();
        var step = ActualWidth / Math.Max(71, values.Length - 1);
        var startX = ActualWidth - step * (values.Length - 1);
        var points = values.Select((value, index) =>
            new Point(startX + index * step, ActualHeight - (value / 100d * (ActualHeight - 10)) - 5)).ToArray();

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(points[0].X, ActualHeight), true, true);
            context.LineTo(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
            context.LineTo(new Point(points[^1].X, ActualHeight), false, false);
        }
        area.Freeze();

        drawingContext.DrawGeometry(_fillBrush, null, area);
        drawingContext.DrawGeometry(null, new Pen(_lineBrush, 3), line);
        drawingContext.DrawEllipse(_lineBrush, new Pen(Brushes.White, 2), points[^1], 5, 5);
    }
}
