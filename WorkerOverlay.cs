using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PixelWorkshop;

internal sealed class WorkerOverlay : Window
{
    private const double SpriteWidth = 24;
    private const double WalkSpeed = 18;
    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly Dictionary<Guid, Figure> figures = [];
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer updates = new() { Interval = TimeSpan.FromSeconds(1) };

    internal WorkerOverlay()
    {
        Title = "픽셀작업소 일꾼";
        Height = 88;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int extendedStyle = -20;
            const int clickThrough = 0x20;
            const int noActivate = 0x08000000;
            SetWindowLongW(hwnd, extendedStyle,
                GetWindowLongW(hwnd, extendedStyle) | clickThrough | noActivate);
        };
        Loaded += (_, _) =>
        {
            UpdateWorkers();
            animation.Start();
            updates.Start();
        };
        Closed += (_, _) =>
        {
            animation.Stop();
            updates.Stop();
        };
        animation.Tick += (_, _) => DrawFrame();
        updates.Tick += (_, _) => UpdateWorkers();
    }

    internal void Place(Rect area, double start)
    {
        Left = start;
        Top = area.Bottom - Height;
        Width = Math.Max(SpriteWidth, Math.Min(560, area.Right - start));
    }

    private void UpdateWorkers()
    {
        try
        {
            var workers = Jobs.Workers();
            var ids = workers.Select(worker => worker.Id).ToHashSet();
            foreach (var id in figures.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                canvas.Children.Remove(figures[id].Sprite);
                canvas.Children.Remove(figures[id].Bubble);
                figures.Remove(id);
            }

            foreach (var worker in workers)
            {
                if (!figures.TryGetValue(worker.Id, out var figure))
                {
                    var sprite = new Sprite(Palette[figures.Count % Palette.Length]);
                    var bubble = new TextBlock
                    {
                        Background = Brushes.WhiteSmoke,
                        Foreground = Brushes.DarkSlateGray,
                        Padding = new Thickness(6, 3, 6, 3),
                        MaxWidth = 210,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Visibility = Visibility.Collapsed
                    };
                    figure = new Figure(sprite, bubble);
                    figures.Add(worker.Id, figure);
                    canvas.Children.Add(sprite);
                    canvas.Children.Add(bubble);
                    Panel.SetZIndex(bubble, 1);
                }

                var status = Jobs.Status(worker.Id);
                if (status is null || status.Updated <= figure.LastStatus)
                    continue;
                figure.LastStatus = status.Updated;
                if (DateTimeOffset.Now - status.Updated < TimeSpan.FromSeconds(12))
                {
                    figure.Bubble.Text = $"{worker.Name} · {status.Message}";
                    figure.BubbleUntil = DateTimeOffset.Now.AddSeconds(5);
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The management window reports settings errors when it is opened.
        }
    }

    private void DrawFrame()
    {
        var now = DateTimeOffset.Now;
        var index = 0;
        foreach (var figure in figures.Values)
        {
            var lane = Width / figures.Count;
            var range = Math.Max(0, lane - SpriteWidth);
            figure.Distance = (figure.Distance + WalkSpeed / 10) % Math.Max(1, range * 2);
            var x = lane * index++ + Math.Min(figure.Distance, Math.Max(0, range * 2 - figure.Distance));
            Canvas.SetLeft(figure.Sprite, x);
            Canvas.SetBottom(figure.Sprite, 0);
            figure.Sprite.Step(figure.Distance <= range);
            figure.Bubble.Visibility = now < figure.BubbleUntil ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(figure.Bubble, Math.Clamp(x, 0, Math.Max(0, Width - 210)));
            Canvas.SetBottom(figure.Bubble, 32);
        }
    }

    private static readonly Color[] Palette =
    [
        Colors.MediumSeaGreen, Colors.Coral, Colors.SkyBlue, Colors.Plum, Colors.Goldenrod
    ];

    private sealed class Figure(Sprite sprite, TextBlock bubble)
    {
        internal Sprite Sprite { get; } = sprite;
        internal TextBlock Bubble { get; } = bubble;
        internal DateTimeOffset LastStatus { get; set; }
        internal DateTimeOffset BubbleUntil { get; set; }
        internal double Distance { get; set; }
    }

    private sealed class Sprite : FrameworkElement
    {
        private readonly Brush outfit;
        private bool facingRight;
        private int frame;

        internal Sprite(Color color)
        {
            Width = SpriteWidth;
            Height = 27;
            outfit = new SolidColorBrush(color);
        }

        internal void Step(bool right)
        {
            facingRight = right;
            frame = (frame + 1) % 4;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            void Pixels(Brush brush, int x, int y, int w, int h) =>
                dc.DrawRectangle(brush, null, new Rect(x * 3, y * 3, w * 3, h * 3));

            Pixels(Brushes.DarkSlateGray, 2, 0, 4, 1);
            Pixels(outfit, 1, 1, 6, 1);
            Pixels(Brushes.Bisque, 2, 2, 4, 2);
            Pixels(Brushes.DarkSlateGray, facingRight ? 5 : 2, 2, 1, 1);
            Pixels(outfit, 2, 4, 4, 3);
            Pixels(Brushes.Bisque, facingRight ? 6 : 1, 5 + frame % 2, 1, 1);
            Pixels(Brushes.DarkSlateGray, frame < 2 ? 1 : 2, 7, 2, frame % 2 + 1);
            Pixels(Brushes.DarkSlateGray, frame < 2 ? 5 : 4, 7, 2, 2 - frame % 2);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
}
