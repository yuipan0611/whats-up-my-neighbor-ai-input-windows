using System.Text.Json;
using NekoChat.Animation;

var path = args[0]; var m = Manifest.Load(path); var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
using var doc = JsonDocument.Parse(File.ReadAllText(path));
foreach (var v in doc.RootElement.GetProperty("travelVectors").EnumerateArray()) {
    Check(AnimationPlayer.Travel(m.Times, v.GetProperty("start").GetInt32(), v.GetProperty("end").GetInt32(), v.GetProperty("elapsed").GetDouble()) == v.GetProperty("expected").GetInt32(), "Native forward/reverse travel differs");
}
foreach (var coat in m.Coats) {
    var folder = Path.Combine(Path.GetDirectoryName(path)!, coat);
    foreach (var source in m.Sources) Check(File.Exists(Path.Combine(folder, $"lift-{source:D2}.png")), "Missing original frame");
    foreach (var (name, clip) in m.Clips) for (int i = 0; i < clip.Count; i++) Check(File.Exists(Path.Combine(folder, $"{name}-{i:D3}.png")), "Missing pose frame");
}
var p = new AnimationPlayer(m);
p.Press(0); p.Drag(0); p.Tick(.849); Check(p.LiftIndex < m.CarryIndex, "Pickup ended early");
p.Tick(.85); Check(p.Mode == "carried" && p.LiftIndex == m.CarryIndex, "850ms pickup");
p.Release(1); p.Tick(1.399); Check(p.Mode == "landing", "Landing ended early");
p.Tick(1.4); Check(p.Mode == "idle" && p.Frame == "lift-35.png", "400ms landing uses native resting frame 35");
p.Tick(99); Check(p.Frame == "lift-35.png", "Landing rebounds or loops");
p.Reset(0); p.Press(0); p.Drag(0); p.Tick(.2); p.Release(.2); p.Tick(.4); Check(p.Mode == "idle" && p.LiftIndex == 0, "Early release reverses pickup");
p.Reset(0); p.Press(0); p.Drag(0); p.Tick(.85); p.Release(.85); p.Tick(.95); var visible = p.LiftIndex;
p.Press(.95); p.Tick(4); Check(p.LiftIndex == visible, "Press during landing must freeze exact frame");
p.Drag(4); p.Tick(4); Check(p.LiftIndex == visible, "Regrab snapped"); p.Tick(5); Check(p.LiftIndex == m.CarryIndex, "Regrab returns to carried");
p.Cancel(5); p.Tick(6); Check(p.Mode == "idle" && !p.Pressed && !p.Dragging, "Cancel stuck in carry");
p.Play("grooming", 0); p.Tick(.8); var frame = p.Frame; p.Pause(true, .8); p.Tick(10); Check(p.Frame == frame, "Pause advanced");
p.Pause(false, 10); p.Tick(10); Check(p.Frame == frame, "Resume skipped"); p.Tick(12.4); Check(p.Mode == "idle", "One-shot did not stop");
p.Play("sleeping", 0); p.Tick(100); Check(p.Mode == "clip", "Sleep loop stopped");
p.Play("walk-left", 0); Check(p.Frame == "walk-left-start-000.png", "Walking must retain native first-step easing");
p.Tick(1); Check(!p.Frame.Contains("-start-", StringComparison.Ordinal), "Walking startup must not repeat each cycle");
p.Reset(101); p.Press(101); p.Release(101.1); Check(p.ClipName == "petted", "Click lost response");
// Foot phase follows distance, including the native first-step easing.
p.Play("walk-left",0); p.SetWalkDistance(AnimationPlayer.StrideDistance * .55);
var halfStride=p.Frame; p.Tick(100);
Check(p.Frame==halfStride && p.Frame.Contains("-start-"),"Stationary paws advanced with time");
p.SetWalkDistance(AnimationPlayer.StrideDistance * 1.55);
Check(!p.Frame.Contains("-start-") && p.Frame.EndsWith("-008.png"),"Distance-linked gait cycle differs");
var still=p.Frame;p.Pause(true,100);p.SetWalkDistance(0);Check(p.Frame==still,"Paused gait moved");
var activity=new DesktopPetActivity(7);activity.Reset(0);
Check(activity.Step(1,500,0,1000).Pose=="sitting","Initial sitting omitted");
Check(activity.Step(2,500,0,1000).Pose=="standing","Native standing transition omitted");
var start=activity.Step(3.8,500,0,1000);
Check(start.Pose is "walk-left" or "walk-right","Native first walk omitted");
var early=activity.Step(4.3,500,0,1000);
var middle=activity.Step(4.8,500,0,1000);
Check(middle.X>=0 && middle.X<=1000 && Math.Abs(early.X-500)<Math.Abs(middle.X-500),"Eased journey left bounds");
Check(Math.Abs(middle.WalkDistance-Math.Abs(middle.X-500))<1e-8,"Gait distance is not actual travel");
var arrived=activity.Step(13, middle.X,0,1000);
Check(arrived.Pose=="standing","Walk completion did not reach native rest");
Check(activity.Step(15.2,arrived.X,0,1000).Pose=="grooming","Missing grooming transition");
Check(activity.Step(18.4,arrived.X,0,1000).Pose=="yawning","Missing yawning transition");
Check(activity.Step(21.6,arrived.X,0,1000).Pose=="loafing","Missing loafing transition");
Check(activity.Step(26.6,arrived.X,0,1000).Pose=="sleeping","Missing sleeping transition");
Check(activity.Step(61,arrived.X,0,1000).Pose is "walk-left" or "walk-right","Sleep did not resume a journey");
activity.Reset(100);Check(activity.Step(101,arrived.X,0,1000).Pose=="sitting","Interrupt did not restore sitting");
// Two-dimensional travel must cover the full work area and link gait to the
// travelled diagonal distance, including a display with no horizontal room.
foreach (var bounds in new[] { (-1800d, -200d, 0d, 900d), (0d, 0d, 0d, 600d) })
{
    var roaming = new DesktopPetActivity(42); roaming.Reset(0, 0);
    double x = (bounds.Item1 + bounds.Item3) / 2, y = (bounds.Item2 + bounds.Item4) / 2;
    var visited = new HashSet<int>(); bool diagonal = false, vertical = false;
    for (int tick = 0; tick < 24000; tick++)
    {
        var step = roaming.Step(tick * .25, x, y, bounds.Item1, bounds.Item3, bounds.Item2, bounds.Item4);
        Check(step.X >= bounds.Item1 && step.X <= bounds.Item3 && step.Y >= bounds.Item2 && step.Y <= bounds.Item4,
            "Two-dimensional journey escaped its monitor");
        vertical |= Math.Abs(step.Y - y) > .01;
        diagonal |= Math.Abs(step.X - x) > .01 && Math.Abs(step.Y - y) > .01;
        if (step.Pose is not "walk-left" and not "walk-right")
            for (int corner = 0; corner < 4; corner++)
                if (Math.Abs(step.X - ((corner & 1) == 0 ? bounds.Item1 : bounds.Item3)) < .001 &&
                    Math.Abs(step.Y - ((corner & 2) == 0 ? bounds.Item2 : bounds.Item4)) < .001) visited.Add(corner);
        x = step.X; y = step.Y;
    }
    Check(vertical && (bounds.Item1 == bounds.Item3 || diagonal), "Vertical or diagonal travel was omitted");
    Check(visited.Count == 4, "Uninterrupted roaming did not visit all corners");
}
var diagonalActivity = new DesktopPetActivity(7); diagonalActivity.Reset(0, 0);
diagonalActivity.Step(0, 500, 500, 0, 1000, 0, 1000);
diagonalActivity.Step(2, 500, 500, 0, 1000, 0, 1000);
var diagonalStep = diagonalActivity.Step(2.5, 500, 500, 0, 1000, 0, 1000);
Check(Math.Abs(diagonalStep.WalkDistance - Math.Sqrt(Math.Pow(diagonalStep.X - 500, 2) + Math.Pow(diagonalStep.Y - 500, 2))) < 1e-8,
    "Diagonal gait ignores vertical distance");
Console.WriteLine($"Animation preview: {checks} checks passed (native travel vectors, nine coats, interrupted drag, landing, pause, two-dimensional roaming and four-corner coverage).");
