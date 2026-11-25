using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProcBridge.Launcher.Wpf.Helpers;

/// <summary>Shift + mouse wheel scrolls lists, grids and trees horizontally (registered once for the whole app).</summary>
public static class HorizontalWheelScroll
{
    public static void Register()
    {
        foreach (Type type in new[] { typeof(DataGrid), typeof(ListBox), typeof(TreeView) })
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift || sender is not DependencyObject element)
            return;
        ScrollViewer? viewer = FindScrollViewer(element);
        if (viewer is null || viewer.ScrollableWidth <= 0)
            return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
            return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } found)
                return found;
        }
        return null;
    }
}
