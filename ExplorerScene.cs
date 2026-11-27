using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace PixelWorkshop;

internal sealed record SceneRequest(Guid WorkerId, string Parent, string Date, bool Rope, string Result, bool Preview = false);

internal sealed class ExplorerScene(SceneRequest request, double homeX)
{
    private const double Walk = 2.6;
    private const double Climb = 2.2;
    private const double Cross = 1.3;
    private const double Descend = 1.5;
    private const double Work = 1.7;
    private readonly Stopwatch age = Stopwatch.StartNew();
    private readonly Stopwatch route = new();
    private readonly Stopwatch retreat = new();
    private ExplorerView? view;
    private bool hadItem;
    private int missedSamples;
    private int missingItemSamples;
    private TimeSpan reactionUntil;
    private string? reaction;
    private Point feet;
    private Point retreatFrom;

    internal SceneRequest Request { get; } = request;
    internal nint Handle => view?.Handle ?? 0;
    internal bool Finished { get; private set; }
    internal bool Interrupted => retreat.IsRunning;
    internal bool NeedsSnapshot => !retreat.IsRunning && !Finished;

    internal void Observe(ExplorerView? snapshot, Rect area)
    {
        if (!NeedsSnapshot)
            return;

        if (snapshot is not null && !snapshot.Item.IsEmpty)
        {
            var visibleItem = Rect.Intersect(snapshot.Item, area);
            snapshot = snapshot with { Item = visibleItem, ItemSelected = !visibleItem.IsEmpty && snapshot.ItemSelected };
        }

        if (view is null)
        {
            if (snapshot is not null && area.IntersectsWith(snapshot.Window))
            {
                view = snapshot;
                hadItem = !snapshot.Item.IsEmpty;
                route.Start();
            }
            return;
        }

        if (snapshot is null || !area.IntersectsWith(snapshot.Window))
        {
            if (++missedSamples >= 2)
                Interrupt("앗, 탐색기가 어디 갔죠?", leave: true);
            return;
        }

        missedSamples = 0;
        if (!string.Equals(snapshot.Folder, view.Folder, StringComparison.OrdinalIgnoreCase))
        {
            Interrupt("앗! 먼저 폴더를 여셨네요!", leave: true);
            return;
        }
        if (hadItem && snapshot.Item.IsEmpty)
        {
            if (++missingItemSamples >= 2)
                Interrupt("앗, 아이콘이 안 보여요!", leave: true);
            return;
        }

        missingItemSamples = 0;
        if (snapshot.ItemSelected && !view.ItemSelected)
        {
            Interrupt("앗! 제가 보고 있던 폴더예요!", leave: true);
            return;
        }

        if ((snapshot.Window.TopLeft - view.Window.TopLeft).Length > 4 ||
            (snapshot.Window.BottomRight - view.Window.BottomRight).Length > 4)
            Interrupt(route.Elapsed.TotalSeconds < Walk
                ? "엇, 탐색기가 움직였어요!"
                : "잠깐만요, 저 아직 매달려 있어요!", leave: false);

        view = snapshot;
        hadItem |= !snapshot.Item.IsEmpty;
    }

    private void Interrupt(string text, bool leave)
    {
        if (leave || age.Elapsed >= reactionUntil)
        {
            reaction = text;
            reactionUntil = age.Elapsed + TimeSpan.FromSeconds(leave ? 1.25 : 1.1);
        }

        if (leave)
        {
            retreatFrom = feet;
            retreat.Start();
        }
    }

    internal SceneFrame Frame(Rect area)
    {
        var floor = area.Bottom;
        if (view is null)
        {
            Finished = age.Elapsed.TotalSeconds >= 2;
            return new SceneFrame(new Point(homeX, floor), true, null, null, null, WorkerPose.Walk);
        }

        var t = route.Elapsed.TotalSeconds;
        var window = Rect.Intersect(view.Window, area);
        if (window.IsEmpty && !retreat.IsRunning)
            Interrupt("앗, 탐색기가 어디 갔죠?", leave: true);

        if (retreat.IsRunning)
        {
            var elapsed = retreat.Elapsed.TotalSeconds;
            var from = new Point(Math.Clamp(retreatFrom.X, area.Left + 12, area.Right - 12), retreatFrom.Y);
            var drop = Blend(from, new Point(from.X, floor), elapsed - 0.55);
            Finished = elapsed >= 2.5;
            var returnHome = Blend(drop, new Point(homeX, floor), elapsed - 1.5);
            return new SceneFrame(new Point(returnHome.X, returnHome.Y - Hop(elapsed)), homeX > from.X,
                null, null, elapsed < 1.25 ? reaction : null,
                elapsed < 1.5 ? WorkerPose.Surprised : WorkerPose.Walk);
        }

        var item = view.Item.IsEmpty ? Rect.Empty : Rect.Intersect(view.Item, area);
        var targetX = item.IsEmpty ? window.Left + window.Width / 2 : item.Left + Math.Min(item.Width / 2, 18);
        var targetY = item.IsEmpty ? window.Top + Math.Min(window.Height * 0.58, 120) : item.Bottom;
        targetX = Math.Clamp(targetX, area.Left + 14, area.Right - 14);
        targetY = Math.Clamp(targetY, area.Top + 28, floor);
        var roofY = Math.Clamp(window.Top, area.Top + 28, targetY);
        var ladderX = Request.Rope
            ? Math.Clamp(window.Left + 18, area.Left + 14, area.Right - 14)
            : targetX;
        var home = new Point(homeX, floor);
        var basePoint = new Point(ladderX, floor);
        var ladderTop = new Point(ladderX, Request.Rope ? roofY : targetY);
        var anchor = new Point(targetX, roofY);
        var goal = new Point(targetX, targetY);
        var travel = Walk + Climb + (Request.Rope ? Cross + Descend : 0);
        var returning = t > travel + Work;
        var progress = returning ? travel * 2 + Work - t : Math.Min(t, travel);
        Finished = progress <= 0 && returning;

        if (progress < Walk)
            feet = Blend(home, basePoint, progress / Walk);
        else if (progress < Walk + Climb)
            feet = Blend(basePoint, ladderTop, (progress - Walk) / Climb);
        else if (Request.Rope && progress < Walk + Climb + Cross)
            feet = Blend(ladderTop, anchor, (progress - Walk - Climb) / Cross);
        else
            feet = Request.Rope ? Blend(anchor, goal, (progress - Walk - Climb - Cross) / Descend) : goal;

        var ladderDeployment = Math.Clamp((progress - Walk + 0.45) / 0.45, 0, 1);
        LineSegment? ladder = ladderDeployment > 0
            ? new LineSegment(Blend(basePoint, ladderTop, ladderDeployment), basePoint)
            : null;
        var ropeDeployment = Math.Clamp((progress - Walk - Climb - Cross + 0.12) / 0.85, 0, 1);
        LineSegment? rope = Request.Rope && ropeDeployment > 0
            ? new LineSegment(anchor, Blend(anchor, goal, ropeDeployment))
            : null;

        var working = t >= travel && !returning;
        var reacting = age.Elapsed < reactionUntil;
        var bob = reacting ? Hop(1.1 - (reactionUntil - age.Elapsed).TotalSeconds) :
            working ? Math.Sin((t - travel) * 13) * 2 : 0;
        var pose = progress >= Walk && (progress < Walk + Climb || Request.Rope && progress >= Walk + Climb + Cross)
            ? WorkerPose.Climb
            : WorkerPose.Walk;
        if (working)
            pose = WorkerPose.Work;
        if (reacting)
            pose = WorkerPose.Surprised;

        return new SceneFrame(new Point(feet.X, feet.Y - bob),
            returning ? homeX > ladderX : progress < Walk ? ladderX > homeX : targetX >= ladderX,
            ladder, rope, reacting ? reaction : working ? Request.Result : null, pose);
    }

    private static double Hop(double seconds) =>
        seconds is >= 0 and <= 0.5 ? Math.Sin(seconds * Math.PI / 0.5) * 13 : 0;

    private static Point Blend(Point start, Point end, double fraction) =>
        start + (end - start) * Math.Clamp(fraction, 0, 1);
}

internal enum WorkerPose { Walk, Climb, Work, Surprised }

internal readonly record struct LineSegment(Point Start, Point End);
internal readonly record struct SceneFrame(Point Feet, bool Right, LineSegment? Ladder, LineSegment? Rope, string? Speech, WorkerPose Pose);

internal sealed class ExplorerProps : FrameworkElement
{
    private static readonly Pen Rail = new(Brushes.SaddleBrown, 4);
    private static readonly Pen Rung = new(Brushes.Peru, 3);
    private static readonly Pen Cord = new(Brushes.Goldenrod, 2);
    private LineSegment? ladder;
    private LineSegment? rope;

    internal void Show(SceneFrame? frame)
    {
        if (ladder == frame?.Ladder && rope == frame?.Rope)
            return;

        ladder = frame?.Ladder;
        rope = frame?.Rope;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ladder is { } rails)
        {
            var x = rails.Start.X;
            dc.DrawLine(Rail, new Point(x - 8, rails.Start.Y), new Point(x - 8, rails.End.Y));
            dc.DrawLine(Rail, new Point(x + 8, rails.Start.Y), new Point(x + 8, rails.End.Y));
            for (var y = rails.End.Y - 7; y >= rails.Start.Y; y -= 11)
                dc.DrawLine(Rung, new Point(x - 8, y), new Point(x + 8, y));
        }

        if (rope is { } line)
        {
            dc.DrawEllipse(Brushes.DarkSlateGray, null, line.Start, 5, 3);
            dc.DrawLine(Cord, line.Start, line.End);
        }
    }
}
