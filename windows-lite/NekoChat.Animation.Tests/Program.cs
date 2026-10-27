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
Console.WriteLine($"Animation preview: {checks} checks passed (native travel vectors, nine coats, interrupted drag, landing, pause and pose playback).");
