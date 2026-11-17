using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Employee.Desktop;

// Horizontal scrolling for every list and table in the app. WPF scrolls only vertically with the wheel,
// ignores sideways touchpad swipes and tilt wheels, and squeezes a table's star column to almost nothing
// instead of letting the table overflow (seen: long tables could not be scrolled sideways at all).
internal static class ScrollAssist
{
    private const int WmMouseHWheel = 0x020E;
    private const double StarColumnMinWidth = 320;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(ShiftWheel), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(HookWindow));
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler(KeepStarColumnsWide));
    }

    // Shift + wheel scrolls sideways.
    private static void ShiftWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || e.Handled)
            return;
        if (Sideways(e.OriginalSource as DependencyObject) is { } viewer)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // Touchpad sideways swipes and tilt wheels arrive as WM_MOUSEHWHEEL, which WPF does not route.
    private static void HookWindow(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && PresentationSource.FromVisual(window) is HwndSource source && !hooked.Contains(source))
        {
            hooked.Add(source);
            source.AddHook(WindowMessage);
            source.Disposed += (_, _) => hooked.Remove(source);
        }
    }

    private static readonly HashSet<HwndSource> hooked = [];

    private static IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseHWheel)
            return IntPtr.Zero;
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        if (Sideways(Mouse.DirectlyOver as DependencyObject) is { } viewer)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // The nearest scroll area under the pointer that can move sideways.
    private static ScrollViewer? Sideways(DependencyObject? element)
    {
        for (var current = element; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ScrollViewer { ScrollableWidth: > 0 } viewer && viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return viewer;
        return null;
    }

    // A star column keeps a readable width, so a wide table overflows and scrolls instead of shrinking it.
    private static void KeepStarColumnsWide(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;
        foreach (var column in grid.Columns)
            if (column.Width.IsStar && column.MinWidth < StarColumnMinWidth)
                column.MinWidth = StarColumnMinWidth;
    }
}
