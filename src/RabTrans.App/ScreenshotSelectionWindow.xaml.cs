using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RabTrans;

public partial class ScreenshotSelectionWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting;

    public Rect? SelectedRegion { get; private set; }
    public Rect? SelectedScreenRegion { get; private set; }

    public ScreenshotSelectionWindow()
    {
        InitializeComponent();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        _isSelecting = true;
        SelectedRegion = null;
        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint);
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isSelecting)
        {
            UpdateSelection(e.GetPosition(this));
        }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        ReleaseMouseCapture();
        var endPoint = e.GetPosition(this);
        var rect = CreateRect(_startPoint, endPoint);

        if (rect.Width >= 8 && rect.Height >= 8)
        {
            SelectedRegion = rect;
            var topLeft = PointToScreen(new Point(rect.Left, rect.Top));
            var bottomRight = PointToScreen(new Point(rect.Right, rect.Bottom));
            SelectedScreenRegion = new Rect(topLeft, bottomRight);
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }

        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void UpdateSelection(Point currentPoint)
    {
        var rect = CreateRect(_startPoint, currentPoint);
        Canvas.SetLeft(SelectionRectangle, rect.Left);
        Canvas.SetTop(SelectionRectangle, rect.Top);
        SelectionRectangle.Width = rect.Width;
        SelectionRectangle.Height = rect.Height;
    }

    private static Rect CreateRect(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
