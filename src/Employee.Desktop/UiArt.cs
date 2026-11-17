using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Employee.Desktop;
public static class UiArt
{
    private static Color ParseColor(string value) => ColorConverter.ConvertFromString(value)is Color color ? color : throw new ArgumentException("Invalid UI color.", nameof(value));
    public static Brush Brush(string color) => new SolidColorBrush(ParseColor(color));
    public static LinearGradientBrush Gradient(string top, string bottom) => new(ParseColor(top), ParseColor(bottom), 90);
    public static FrameworkElement Icon(string key, double size = 24, string color = "#C6B9F0")
    {
        var geometry = key switch
        {
            "home" => "M3,11 L12,3 21,11 M5,10 V21 H10 V15 H14 V21 H19 V10",
            // Office building with windows (the Virtual Office tab).
            "office" => "M2,22 H22 M4,22 V3 H14 V22 M14,9 H20 V22 M7,7 H8 M10,7 H11 M7,11 H8 M10,11 H11 M7,15 H8 M10,15 H11 M8,22 V18 H10 V22 M17,13 H18 M17,17 H18",
            "projects" => "M3,7 L12,2 21,7 12,12 Z M3,12 L12,17 21,12 M3,17 L12,22 21,17",
            "tasks" => "M9,5 H22 M9,12 H22 M9,19 H22 M3,5 H4 M3,12 H4 M3,19 H4",
            "agents" => "M9,12 A4,4 0 1 0 9,4 A4,4 0 1 0 9,12 M2,21 V19 Q2,14 9,14 Q16,14 16,19 V21 M17,4 Q23,8 17,12 M19,15 Q23,16 23,21",
            "providers" => "M12,2 L21,7 V17 L12,22 3,17 V7 Z M3,7 L12,12 21,7 M12,12 V22 M7,5 L17,10",
            "local" => "M6,6 H18 V18 H6 Z M9,9 H15 V15 H9 Z M8,2 V6 M12,2 V6 M16,2 V6 M8,18 V22 M12,18 V22 M16,18 V22 M2,8 H6 M2,12 H6 M2,16 H6 M18,8 H22 M18,12 H22 M18,16 H22",
            "tools" => "M3,3 H9 V9 H3 Z M15,3 H21 V9 H15 Z M3,15 H9 V21 H3 Z M15,15 H21 V21 H15 Z",
            "workflows" => "M9,8 L15,4 M9,16 L15,20 M7,10 V14 M21,5 A3,3 0 1 0 15,5 A3,3 0 1 0 21,5 M9,7 A3,3 0 1 0 3,7 A3,3 0 1 0 9,7 M9,17 A3,3 0 1 0 3,17 A3,3 0 1 0 9,17 M21,20 A3,3 0 1 0 15,20 A3,3 0 1 0 21,20",
            "activity" => "M5,2 H15 L20,7 V22 H5 Z M14,2 V8 H20 M8,12 H16 M8,16 H16",
            "approvals" => "M21,10 A9,9 0 1 1 16,3 M8,11 L12,15 22,4",
            "guide" => "M4,3 H20 V21 H4 Z M8,8 H16 M8,12 H16 M8,16 H13",
            "settings" => "M9,3 H15 L16,6 19,7 22,7 V13 L19,14 17,17 17,21 H11 L10,18 7,16 3,16 V10 L6,9 8,6 Z M16,12 A4,4 0 1 0 8,12 A4,4 0 1 0 16,12",
            "updates" => "M4,8 A9,9 0 1 1 3,16 M4,3 V9 H10 M12,17 V8 M8,12 L12,8 16,12",
            "code" => "M7,5 L1,12 7,19 M17,5 L23,12 17,19 M15,2 L9,22",
            "game" => "M7,6 H17 Q21,6 22,12 L23,17 Q23,22 19,20 L15,16 H9 L5,20 Q1,22 1,17 L2,12 Q3,6 7,6 Z M5,10 V16 M2,13 H8 M17,10 H18 M19,14 H20",
            "image" => "M3,3 H21 V21 H3 Z M3,17 L9,11 14,16 17,13 21,17 M16,8 A2,2 0 1 0 12,8 A2,2 0 1 0 16,8",
            "video" => "M3,5 H16 V19 H3 Z M16,10 L23,6 V18 L16,14",
            "marketing" => "M3,9 H8 L20,3 V21 L8,15 H3 Z M8,15 L10,22 H6 L4,15 M23,9 V15",
            "search" => "M18,10 A8,8 0 1 0 2,10 A8,8 0 1 0 18,10 M16,16 L23,23",
            "idea" => "M8,16 Q1,10 6,4 Q12,-1 18,4 Q23,10 16,16 V19 H8 Z M9,22 H15 M12,16 V10 M9,8 L12,11 15,8",
            "rocket" => "M8,15 Q9,5 22,2 Q21,15 11,17 Z M8,9 H4 L1,16 8,15 M15,16 L16,23 22,18 V13 M5,19 L1,23 M18,8 A2,2 0 1 0 14,8 A2,2 0 1 0 18,8",
            "coffee" => "M3,8 H17 V17 Q17,22 10,22 Q3,22 3,17 Z M17,9 H20 Q25,13 20,16 H17 M6,2 V5 M11,1 V5",
            "folder" => "M2,5 H9 L12,8 H22 V21 H2 Z M2,10 H22",
            "chart" => "M3,21 V13 H6 V21 Z M10,21 V8 H13 V21 Z M17,21 V2 H20 V21 Z",
            "monitor" => "M2,3 H22 V17 H2 Z M12,17 V22 M7,22 H17",
            "pulse" => "M1,13 H6 L9,4 13,21 17,10 19,13 H23",
            "clock" => "M22,12 A10,10 0 1 0 2,12 A10,10 0 1 0 22,12 M12,6 V12 L16,15",
            "switch" => "M2,7 H22 L17,2 M22,17 H2 L7,22",
            "pause" => "M8,3 V21 M17,3 V21",
            "stop" => "M5,5 H19 V19 H5 Z",
            "plus" => "M12,3 V21 M3,12 H21",
            "arrow" => "M3,12 H22 L16,6 M22,12 L16,18",
            "bell" => "M5,17 V10 Q5,3 12,3 Q19,3 19,10 V17 L22,20 H2 Z M9,23 H15",
            "more" => "M3,12 H4 M11,12 H12 M19,12 H20",
            "spark" => "M12,1 L15,9 23,12 15,15 12,23 9,15 1,12 9,9 Z M3,2 V6 M1,4 H5 M21,19 V23 M19,21 H23",
            _ => "M21,12 A9,9 0 1 0 3,12 A9,9 0 1 0 21,12"
        };
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24
        };
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = Brush(color), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            Stretch = Stretch.Uniform
        };
    }

    public static FrameworkElement Badge(string key, double size = 44, string color = "#B899FF") => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 4),
        Background = Gradient("#413460", "#27233E"),
        BorderBrush = Brush("#55466E"),
        BorderThickness = new Thickness(1),
        Child = Icon(key, size * 0.55, color)
    };
    public static FrameworkElement Notification(int count)
    {
        var root = new Grid
        {
            Width = 28,
            Height = 28
        };
        root.Children.Add(Icon("bell", 20));
        if (count > 0)
            root.Children.Add(new Border { Child = new TextBlock { Text = count > 99 ? "99+" : count.ToString(), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brush("#FFFFFF"), TextAlignment = TextAlignment.Center }, Background = Brush("#8653D4"), CornerRadius = new CornerRadius(8), Padding = new Thickness(3, 1, 3, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, -4, -5, 0) });
        return root;
    }

    public static FrameworkElement ProjectThumbnail(string name, double size = 44)
    {
        var art = new Canvas
        {
            Width = 64,
            Height = 64,
            Clip = new RectangleGeometry(new Rect(0, 0, 64, 64), 10, 10),
            Background = Gradient("#7790D8", "#5C427E")
        };
        void Add(FrameworkElement shape, double x, double y)
        {
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
            art.Children.Add(shape);
        }

        void Rect(double x, double y, double width, double height, string color) => Add(new Rectangle { Width = width, Height = height, Fill = Brush(color) }, x, y);
        void Oval(double x, double y, double width, double height, string color) => Add(new Ellipse { Width = width, Height = height, Fill = Brush(color) }, x, y);
        void Path(string data, string color) => art.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Fill = Brush(color) });
        if (name.Contains("garden", StringComparison.OrdinalIgnoreCase))
        {
            art.Background = Gradient("#9ACDD8", "#659293");
            Oval(-12, 32, 85, 40, "#45764E");
            Oval(7, 42, 55, 24, "#82A669");
            Oval(7, 49, 40, 10, "#68B1BC");
            Rect(25, 24, 4, 24, "#72463D");
            Rect(45, 24, 4, 24, "#72463D");
            Rect(25, 24, 24, 4, "#70443B");
            Rect(24, 45, 28, 4, "#B59D72");
            Path("M17,25 Q31,21 36,12 Q44,22 57,24 L53,29 22,29 Z", "#8A3D45");
            Path("M21,24 L36,16 52,24 Z", "#D39668");
            Rect(10, 22, 3, 33, "#665254");
            Path("M11,35 L2,22 5,20 13,30 20,17 22,19 Z", "#665254");
            foreach (var point in new[]
            {
                (5, 20),
                (13, 15),
                (20, 18),
                (4, 29),
                (17, 26)
            }

            )
            {
                Oval(point.Item1 - 5, point.Item2 - 4, 13, 11, "#DEA0BD");
                Oval(point.Item1, point.Item2, 7, 7, "#F3C6D4");
            }

            Oval(49, 42, 14, 15, "#375D42");
        }
        else if (name.Contains("cat", StringComparison.OrdinalIgnoreCase) || name.Contains("pond", StringComparison.OrdinalIgnoreCase))
        {
            art.Background = Gradient("#A599EF", "#60599B");
            Path("M13,27 L11,9 27,20 38,20 52,9 51,30 Z", "#F6ECE7");
            Path("M15,23 L15,14 23,22 Z M43,22 L49,14 48,26 Z", "#EAA77C");
            Oval(11, 19, 42, 36, "#FFF6E8");
            Path("M13,25 L26,20 25,32 15,35 Z", "#DA9F72");
            Oval(22, 33, 5, 7, "#342E42");
            Oval(39, 33, 5, 7, "#342E42");
            Path("M29,41 L35,41 32,45 Z", "#BE7E89");
            Rect(22, 53, 23, 3, "#564678");
        }
        else if (name.Contains("pixel", StringComparison.OrdinalIgnoreCase))
        {
            art.Background = Gradient("#B99BF0", "#6C50A3");
            Path("M10,48 L16,36 42,10 54,22 28,48 Z", "#EAC88C");
            Path("M18,35 L39,14 49,24 28,45 Z", "#F9DB7A");
            Path("M40,13 L44,9 56,21 51,26 Z", "#D394CA");
            Path("M10,48 L16,36 24,44 Z", "#FFF3CB");
            Path("M10,48 L13,41 17,45 Z", "#3C344A");
            Rect(14, 51, 30, 3, "#533E7A");
        }
        else if (name.Contains("video", StringComparison.OrdinalIgnoreCase) || name.Contains("trailer", StringComparison.OrdinalIgnoreCase))
        {
            art.Background = Gradient("#A594EF", "#6059A1");
            Add(new Rectangle { Width = 46, Height = 43, RadiusX = 7, RadiusY = 7, Fill = Brush("#30274D") }, 9, 11);
            Rect(20, 15, 24, 34, "#8B83D8");
            for (var y = 16; y < 49; y += 10)
            {
                Rect(12, y, 5, 5, "#BFB1EF");
                Rect(47, y, 5, 5, "#BFB1EF");
            }

            Path("M27,24 L39,32 27,40 Z", "#E3D5FD");
        }
        else
            return Badge("game", size);
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = art,
            Stretch = Stretch.Uniform
        };
    }
}
