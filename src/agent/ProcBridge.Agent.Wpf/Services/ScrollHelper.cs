using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProcBridge.Agent.Wpf.Services;

/// <summary>Shift + mouse wheel scrolls grids and lists horizontally (WPF has no such default).</summary>
public static class ScrollHelper
{
    public static void RegisterShiftWheelHorizontalScroll()
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
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(element, i));
            if (found is not null)
                return found;
        }
        return null;
    }
}
