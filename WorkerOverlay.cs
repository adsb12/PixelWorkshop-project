using System.Globalization;
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
    private readonly Queue<SceneRequest> pending = new();
    private readonly ExplorerProps props = new();
    private readonly Canvas sceneLayer = new();
    private Guid? sceneWorker;
    private nint clippedExplorer;
    private DateTimeOffset nextClip;
    private Rect lastArea = Rect.Empty;
    private Rect lastExplorer = Rect.Empty;
    private List<Rect> lastCovers = [];
    private ExplorerScene? scene;
    private (ExplorerScene Scene, Task<ExplorerView?> Read)? probe;
    private DateTimeOffset nextProbe;
    private double patrolStart;
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer updates = new() { Interval = TimeSpan.FromSeconds(1) };

    internal WorkerOverlay()
    {
        Title = "픽셀작업소 일꾼";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Content = canvas;
        canvas.Children.Add(sceneLayer);
        sceneLayer.Children.Add(props);
        Panel.SetZIndex(props, -1);

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
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
        sceneLayer.Width = area.Width;
        sceneLayer.Height = area.Height;
        clippedExplorer = 0;
        props.Width = area.Width;
        props.Height = area.Height;
        patrolStart = start - area.Left;
    }

    internal void Preview(Worker worker, bool rope)
    {
        UpdateWorkers();
        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Enqueue(new SceneRequest(worker.Id, worker.Parent, date, rope, "도착했어요!", Preview: true));
    }

    private void Enqueue(SceneRequest request)
    {
        if (pending.Count < Jobs.MaxWorkers && !pending.Contains(request))
            pending.Enqueue(request);
    }

    private void UpdateWorkers()
    {
        try
        {
            var workers = Jobs.Workers();
            var ids = workers.Select(worker => worker.Id).ToHashSet();
            foreach (var id in figures.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var figure = figures[id];
                if (figure.Sprite.Parent is Panel layer)
                {
                    layer.Children.Remove(figure.Sprite);
                    layer.Children.Remove(figure.Bubble);
                }
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
                    var result = $"{worker.Name} · {status.Message}";
                    if (status.FolderDate is { } date)
                    {
                        var rope = TimeOnly.FromDateTime(status.Updated.LocalDateTime) >= worker.CleanAt;
                        Enqueue(new SceneRequest(worker.Id, worker.Parent, date, rope, result));
                    }
                    else
                    {
                        Speak(figure, result);
                    }
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
        var area = new Rect(0, 0, Width, Height);
        if (scene is not null && !figures.ContainsKey(scene.Request.WorkerId))
            scene = null;

        while (scene is null && pending.TryDequeue(out var next))
        {
            if (figures.TryGetValue(next.WorkerId, out var worker))
            {
                var x = Canvas.GetLeft(worker.Sprite);
                scene = new ExplorerScene(next, double.IsFinite(x) ? x + SpriteWidth / 2 : patrolStart + SpriteWidth);
                nextProbe = now;
            }
        }

        TrackExplorer(now, area);
        var sceneFrame = scene?.Frame(area);
        PlaceSceneWorker(scene?.Request.WorkerId);
        UpdateSceneClip(now, area, scene?.Handle ?? 0);
        props.Show(sceneFrame);

        var index = 0;
        foreach (var (id, figure) in figures)
        {
            var lane = Math.Max(0, Math.Min(560, Width - patrolStart)) / figures.Count;
            var range = Math.Max(0, lane - SpriteWidth);
            figure.Distance = (figure.Distance + WalkSpeed * 0.033) % Math.Max(1, range * 2);
            var x = patrolStart + lane * index++ + Math.Min(figure.Distance, Math.Max(0, range * 2 - figure.Distance));
            var feet = new Point(x + SpriteWidth / 2, area.Bottom);
            var right = figure.Distance <= range;
            var pose = WorkerPose.Walk;
            var speech = now < figure.BubbleUntil ? figure.Message : null;
            if (scene?.Request.WorkerId == id && sceneFrame is { } frame)
            {
                feet = frame.Feet;
                right = frame.Right;
                pose = frame.Pose;
                speech = frame.Speech ?? speech;
            }

            Canvas.SetLeft(figure.Sprite, feet.X - SpriteWidth / 2);
            Canvas.SetTop(figure.Sprite, feet.Y - figure.Sprite.Height);
            figure.Sprite.Step(right, pose);
            figure.Bubble.Visibility = speech is null ? Visibility.Collapsed : Visibility.Visible;
            if (speech is not null)
                figure.Bubble.Text = speech;
            Canvas.SetLeft(figure.Bubble, Math.Clamp(feet.X - 10, 0, Math.Max(0, Width - 210)));
            Canvas.SetTop(figure.Bubble, Math.Max(0, feet.Y - 54));
        }

        if (scene is { Finished: true } finished)
        {
            if (finished.Handle == 0 && figures.TryGetValue(finished.Request.WorkerId, out var figure))
            {
                var text = finished.Request.Preview ? "대상 폴더가 열린 탐색기 창을 찾아 주세요." : finished.Request.Result;
                Speak(figure, text);
            }
            else if (finished.Interrupted && !finished.Request.Preview &&
                figures.TryGetValue(finished.Request.WorkerId, out var interrupted))
            {
                Speak(interrupted, finished.Request.Result);
            }
            scene = null;
        }
    }

    private void PlaceSceneWorker(Guid? id)
    {
        if (sceneWorker == id)
            return;

        if (sceneWorker is { } previous && figures.TryGetValue(previous, out var leaving))
            Move(leaving, sceneLayer, canvas);
        if (id is { } next && figures.TryGetValue(next, out var joining))
            Move(joining, canvas, sceneLayer);
        sceneWorker = id;

        static void Move(Figure figure, Canvas from, Canvas to)
        {
            from.Children.Remove(figure.Sprite);
            from.Children.Remove(figure.Bubble);
            to.Children.Add(figure.Sprite);
            to.Children.Add(figure.Bubble);
        }
    }

    private void UpdateSceneClip(DateTimeOffset now, Rect area, nint explorer)
    {
        if (explorer == 0)
        {
            clippedExplorer = 0;
            sceneLayer.Clip = null;
            sceneLayer.Visibility = Visibility.Visible;
            return;
        }

        var changed = explorer != clippedExplorer || area != lastArea;
        if (!changed && now < nextClip)
            return;

        nextClip = now.AddMilliseconds(75);
        clippedExplorer = explorer;
        lastArea = area;
        var screen = new Rect(PointToScreen(area.TopLeft), PointToScreen(area.BottomRight));
        var (bounds, covers) = ExplorerTracking.ReadOcclusion(explorer, new WindowInteropHelper(this).Handle, screen);
        if (!changed && bounds == lastExplorer && covers.SequenceEqual(lastCovers))
            return;

        lastExplorer = bounds;
        lastCovers = covers;
        // Some hide/close reactions still use the last known scene position.
        var mask = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var cover in covers)
            mask.Children.Add(new RectangleGeometry(ToLocal(cover)));

        var fullyCovered = !bounds.IsEmpty && covers.Count != 0 &&
            Geometry.Combine(new RectangleGeometry(ToLocal(bounds)), mask, GeometryCombineMode.Exclude, null).IsEmpty();
        sceneLayer.Visibility = fullyCovered ? Visibility.Hidden : Visibility.Visible;
        sceneLayer.Clip = mask.Children.Count == 0 ? null :
            new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), mask);
    }

    private void TrackExplorer(DateTimeOffset now, Rect area)
    {
        if (probe is { } ready && ready.Read.IsCompleted)
        {
            probe = null;
            if (ready.Read.IsFaulted)
                System.Diagnostics.Debug.WriteLine(ready.Read.Exception);
            if (scene == ready.Scene)
            {
                var snapshot = ready.Read.IsCompletedSuccessfully ? ready.Read.Result : null;
                if (snapshot is not null)
                    snapshot = snapshot with { Window = ToLocal(snapshot.Window), Item = ToLocal(snapshot.Item) };
                scene.Observe(snapshot, area);
            }
        }

        if (probe is null && scene is { NeedsSnapshot: true } current && now >= nextProbe)
        {
            nextProbe = now.AddMilliseconds(250);
            var handle = current.Handle;
            var request = current.Request;
            var screenArea = new Rect(PointToScreen(area.TopLeft), PointToScreen(area.BottomRight));
            probe = (current, Task.Run(() => ExplorerTracking.Read(request.Parent, request.Date, handle, screenArea)));
        }
    }

    private Rect ToLocal(Rect screen) => screen.IsEmpty ? Rect.Empty :
        new Rect(PointFromScreen(screen.TopLeft), PointFromScreen(screen.BottomRight));

    private static void Speak(Figure figure, string text)
    {
        figure.Message = text;
        figure.BubbleUntil = DateTimeOffset.Now.AddSeconds(5);
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
        internal string? Message { get; set; }
        internal DateTimeOffset BubbleUntil { get; set; }
        internal double Distance { get; set; }
    }

    private sealed class Sprite : FrameworkElement
    {
        private readonly Brush outfit;
        private bool facingRight;
        private int frame;
        private int ticks;
        private WorkerPose pose;

        internal Sprite(Color color)
        {
            Width = SpriteWidth;
            Height = 27;
            outfit = new SolidColorBrush(color);
        }

        internal void Step(bool right, WorkerPose newPose)
        {
            var frameChanged = ++ticks % 3 == 0;
            if (frameChanged)
                frame = (frame + 1) % 4;
            if (frameChanged || pose != newPose || facingRight != right)
                InvalidateVisual();
            facingRight = right;
            pose = newPose;
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
            var swing = frame % 2;
            switch (pose)
            {
                case WorkerPose.Climb:
                    Pixels(Brushes.Bisque, 1, 3 + swing * 2, 1, 2);
                    Pixels(Brushes.Bisque, 6, 5 - swing * 2, 1, 2);
                    break;
                case WorkerPose.Work:
                    Pixels(Brushes.Bisque, facingRight ? 6 : 0, 4 + swing, 2, 1);
                    break;
                case WorkerPose.Surprised:
                    Pixels(Brushes.Bisque, 0, 3, 2, 2);
                    Pixels(Brushes.Bisque, 6, 3, 2, 2);
                    Pixels(Brushes.DarkSlateGray, 3, 3, 1, 1);
                    break;
                default:
                    Pixels(Brushes.Bisque, facingRight ? 6 : 1, 5 + swing, 1, 1);
                    break;
            }
            Pixels(Brushes.DarkSlateGray, frame < 2 ? 1 : 2, 7, 2, swing + 1);
            Pixels(Brushes.DarkSlateGray, frame < 2 ? 5 : 4, 7, 2, 2 - swing);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
}
