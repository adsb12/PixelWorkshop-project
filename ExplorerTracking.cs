using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace PixelWorkshop;

internal sealed record ExplorerView(nint Handle, string Folder, Rect Window, Rect Item, bool ItemSelected);

internal static class ExplorerTracking
{
    // Shell and UI Automation calls run on a background thread. Return only snapshots to WPF.
    internal static ExplorerView? Read(string parent, string date, nint trackedHandle, Rect screenArea)
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return null;

            shell = Activator.CreateInstance(type);
            windows = ((dynamic)shell!).Windows();
            ExplorerView? best = null;
            var bestRank = int.MinValue;
            var foreground = GetForegroundWindow();
            var previousPath = Path.GetDirectoryName(parent);
            var datePath = Path.Combine(parent, date);
            int count = ((dynamic)windows!).Count;
            for (var index = 0; index < count; index++)
            {
                object? explorer = null;
                try
                {
                    explorer = ((dynamic)windows).Item(index);
                    if (explorer is null)
                        continue;

                    nint handle = new(Convert.ToInt64(((dynamic)explorer).HWND, CultureInfo.InvariantCulture));
                    if ((trackedHandle != 0 && trackedHandle != handle) ||
                        !IsWindowVisible(handle) || IsIconic(handle))
                        continue;

                    string executable = ((dynamic)explorer).FullName;
                    if (!string.Equals(Path.GetFileName(executable), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string location = ((dynamic)explorer).LocationURL;
                    var folder = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile
                        ? Path.TrimEndingDirectorySeparator(uri.LocalPath)
                        : location;

                    var inParent = Same(folder, parent);
                    var inPrevious = Same(folder, previousPath);
                    var inDate = Same(folder, datePath);
                    if (trackedHandle == 0 && !inParent && !inPrevious)
                        continue;

                    var bounds = FrameBounds(handle);
                    if (bounds.IsEmpty || !screenArea.IntersectsWith(bounds))
                        continue;

                    var name = inParent ? date : inPrevious ? Path.GetFileName(parent) : null;
                    var (item, selected) = name is null ? (Rect.Empty, false) : FindItem(handle, name, bounds);
                    var rank = (inParent ? 4 : inDate ? 3 : inPrevious ? 2 : 1) * 4 +
                        (item.IsEmpty ? 0 : 2) + (handle == foreground ? 1 : 0);
                    if (rank > bestRank)
                    {
                        bestRank = rank;
                        best = new ExplorerView(handle, folder, bounds, item, selected);
                    }
                }
                catch (COMException)
                {
                    // An Explorer window can close while ShellWindows is being enumerated.
                }
                finally { Release(explorer); }
            }
            return best;
        }
        catch (COMException) { return null; }
        finally
        {
            Release(windows);
            Release(shell);
        }
    }

    private static (Rect, bool) FindItem(nint handle, string name, Rect window)
    {
        try
        {
            var root = AutomationElement.FromHandle(handle);
            var view = root.FindFirst(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ItemsView"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "UIItemsView"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "SysListView32")));
            if (view is null)
                return (Rect.Empty, false);

            var item = view.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase),
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))));
            if (item is null || item.Current.IsOffscreen)
                return (Rect.Empty, false);

            var bounds = Rect.Intersect(Rect.Intersect(item.Current.BoundingRectangle, view.Current.BoundingRectangle), window);
            var selected = item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                ((SelectionItemPattern)pattern).Current.IsSelected;
            return (bounds, selected);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return (Rect.Empty, false);
        }
    }

    private static bool Same(string path, string? other) =>
        string.Equals(path, other, StringComparison.OrdinalIgnoreCase);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    internal static (Rect Explorer, List<Rect> Covers) ReadOcclusion(nint explorer, nint overlay, Rect area)
    {
        const uint previousWindow = 3; // GW_HWNDPREV, toward the front of the Z order
        const int cloakedAttribute = 14;
        var covers = new List<Rect>();
        var bounds = Rect.Intersect(FrameBounds(explorer), area);
        if (bounds.IsEmpty || !IsWindowVisible(explorer) || IsIconic(explorer))
            return (Rect.Empty, covers);

        var visited = new HashSet<nint> { explorer };
        for (var window = GetWindow(explorer, previousWindow); window != 0 && visited.Add(window);
            window = GetWindow(window, previousWindow))
        {
            if (window == overlay || !IsWindowVisible(window) || IsIconic(window))
                continue;

            if (DwmGetWindowAttribute(window, cloakedAttribute, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                continue;

            var cover = Rect.Intersect(FrameBounds(window), area);
            if (!cover.IsEmpty && cover.Width > 0 && cover.Height > 0)
                covers.Add(cover);
        }

        return (bounds, covers);
    }

    private static Rect FrameBounds(nint handle)
    {
        const int extendedFrameBounds = 9;
        if (DwmGetWindowAttribute(handle, extendedFrameBounds, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(handle, out rect))
            return Rect.Empty;

        return rect.Right > rect.Left && rect.Bottom > rect.Top
            ? new Rect(new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Bottom))
            : Rect.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out NativeRect rect, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
