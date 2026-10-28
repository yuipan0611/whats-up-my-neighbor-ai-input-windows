using System.Net;
using System.Text;
using System.Text.Json;
using NekoChat.Core;

int checks=0;
void Check(bool result,string description) {checks++;if(!result)throw new Exception(description);}
void Reject(Action action,string name) {bool rejected=false;try{action();}catch(Exception ex)when(ex is ArgumentException or InvalidDataException or JsonException or InvalidOperationException or FormatException){rejected=true;}Check(rejected,name);}
using(var animation=new NekoChat.Animation.AnimationAssets()) {
    var m=animation.Manifest;Check(m.Coats.SequenceEqual(Appearance.Presets),"animation and appearance preset order agree");
    Check(Math.Abs(m.Times[m.CarryIndex]-.85)<1e-7&&Math.Abs(m.Times[^1]-m.Times[m.CarryIndex]-.4)<1e-7,"native pickup/landing timing preserved");
    foreach(var id in m.Coats) {
        var files=m.Sources.Select(s=>$"lift-{s:D2}.png").Concat(m.Clips.SelectMany(c=>Enumerable.Range(0,c.Value.Count).Select(i=>$"{c.Key}-{i:D3}.png")));
        foreach(var frame in files) {
            using var stream=animation.OpenFrame(id,frame);var header=new byte[24];stream.ReadExactly(header);
            Check(header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"bundled native frame is PNG");
        }
    }
    var player=new NekoChat.Animation.AnimationPlayer(m);player.Press(0);player.Drag(0);player.Tick(.85);
    Check(player.Mode=="carried","native carry transition");player.Release(1);player.Tick(1.4);Check(player.Frame=="lift-35.png","native landing stops at approved frame");
}
string Json(string id)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Coats",id+".json"));
foreach(var id in Appearance.Presets) {
    var a=Appearance.Load(Json(id),Json);Check(a.Preset==id&&a.Parts.Count==16,"full existing coat "+id);
}
using(var file=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Assets","tuxedo.bin.gz")))
using(var gzip=new System.IO.Compression.GZipStream(file,System.IO.Compression.CompressionMode.Decompress))
using(var bytes=new MemoryStream()) {
    gzip.CopyTo(bytes);var mask=new TuxedoMask(bytes.ToArray());
    using var references=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Assets","tuxedo-reference.json")));
    foreach(var item in references.RootElement.EnumerateArray()) {
        var coverage=mask.Coverage(item.GetProperty("chest").GetBoolean(),item.GetProperty("u").GetDouble(),item.GetProperty("v").GetDouble());
        Check(Math.Abs(coverage-item.GetProperty("expected").GetDouble())<.025,"native tuxedo curved-blaze coverage parity");
    }
    Check(mask.Coverage(false,.5,.82)==1&&mask.Coverage(false,.25,.68)==0,"complete white muzzle and protected dark eye region");
    Reject(()=>new TuxedoMask(new byte[5]),"incomplete tuxedo coverage rejected");
}
var custom=Appearance.Load("""{"version":2,"appearance":{"preset":"white","seed":99,"parts":{"head":{"color":"#aabbcc","pattern":"tabby","patternColor":"#123456"}}}}""",Json);
Check(custom.Seed==99&&custom.Parts["head"].Color=="#AABBCC"&&custom.Parts["tail"].Color=="#F4EFE5","part override and fallback");
Reject(()=>Appearance.Load("""{"version":2,"appearance":{"preset":"white","parts":{"shell":{"color":"#000000"}}}}""",Json),"unknown anatomy");
Reject(()=>Appearance.Load("""{"version":2,"appearance":{"preset":"white","parts":{"head":{"color":"red"}}}}""",Json),"bad color");
Check(Protocol.ValidText(string.Concat(Enumerable.Repeat("🐈",2000))),"Unicode scalar limit");
Check(!Protocol.ValidText(string.Concat(Enumerable.Repeat("🐈",2001))),"Unicode scalar overflow");
Check(!Protocol.ValidText(" \r\n\t"),"whitespace");
foreach(var user in new[]{"../bob","A","a b","a\n","",new string('a',33)})Check(!Protocol.ValidUser(user),"bad user "+user);
var raw=Protocol.Send("bob","下課見 🐈","id_1");using(var doc=JsonDocument.Parse(raw))Check(doc.RootElement.GetProperty("text").GetString()=="下課見 🐈","send encoding");
Reject(()=>Protocol.Send("bob","","id"),"blank send");
Check(Protocol.ParseServer("""{"protocol":"nekochat","version":1,"kind":"chat.message","id":"i","sender":"alice","text":"hello"}""").Sender=="alice","receive chat");
foreach(var kind in new[]{"executeRequest","agent.run","chat.send","paste"})Reject(()=>Protocol.ParseServer($$"""{"protocol":"nekochat","version":1,"kind":"{{kind}}","id":"i"}"""),"remote event rejected");
foreach(var json in new[]{
    """{"protocol":"nekochat","version":1,"kind":"ready","id":"i","user":"alice","action":"paste"}""",
    """{"protocol":"office","version":1,"kind":"ready","id":"i","user":"alice"}""",
    """{"protocol":"nekochat","version":true,"kind":"ready","id":"i","user":"alice"}""",
    """{"protocol":"nekochat","version":1.0,"kind":"ready","id":"i","user":"alice"}""",
    """{"protocol":"nekochat","version":1,"kind":"ready","id":"i","user":"alice","user":"bob"}""",
    """{"protocol":"nekochat","version":2,"kind":"ready","id":"i","user":"alice"}""",
    """{"protocol":"nekochat","version":1,"kind":"error","id":"i","code":"run_shell"}"""
})Reject(()=>Protocol.ParseServer(json),"invalid server packet");
foreach(var url in new[]{"ws://example.com/nekochat/v1/ws","ws://8.8.8.8/nekochat/v1/ws","ws://127.0.0.1/office/ws","wss://u:p@example.com/nekochat/v1/ws","wss://example.com/nekochat/v1/ws?token=secret","https://example.com/nekochat/v1/ws"})
    Reject(()=>RelayConnection.ValidateEndpoint(url,true),"endpoint restrictions");
Reject(()=>RelayConnection.ValidateEndpoint("ws://192.168.1.5/nekochat/v1/ws",false),"VPS needs WSS");
Check(RelayConnection.ValidateEndpoint("ws://192.168.1.5/nekochat/v1/ws",true).Scheme=="ws","explicit private LAN");
Check(RelayConnection.ValidateEndpoint("wss://example.com/nekochat/v1/ws",false).Scheme=="wss","public WSS");

if(args.Length==2&&args[0]=="--live") {
    var port=args[1];await using var alice=new RelayConnection();await using var bob=new RelayConnection();
    using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var url=RelayConnection.ValidateEndpoint($"ws://127.0.0.1:{port}/nekochat/v1/ws",true);
    await alice.ConnectAsync(url,"alice","test-only-alice-not-a-real-credential",cancel.Token);
    await bob.ConnectAsync(url,"bob","test-only-bob-not-a-real-credential",cancel.Token);
    var ar=new TaskCompletionSource();var br=new TaskCompletionSource();var received=new TaskCompletionSource<ChatEvent>();var ack=new TaskCompletionSource<ChatEvent>();
    var at=alice.ReceiveAsync(e=>{if(e.Kind=="ready")ar.TrySetResult();if(e.Kind=="chat.ack")ack.TrySetResult(e);},cancel.Token);
    var bt=bob.ReceiveAsync(e=>{if(e.Kind=="ready")br.TrySetResult();if(e.Kind=="chat.message")received.TrySetResult(e);},cancel.Token);
    await Task.WhenAll(ar.Task,br.Task).WaitAsync(cancel.Token);
    await alice.SendAsync("bob","C# ↔ FastAPI 🐈","dotnet_live",cancel.Token);
    Check((await received.Task.WaitAsync(cancel.Token)).Text=="C# ↔ FastAPI 🐈","C# real Relay delivery");
    Check((await ack.Task.WaitAsync(cancel.Token)).Status=="delivered","C# live ack");
    cancel.Cancel();try{await Task.WhenAll(at,bt);}catch(OperationCanceledException){}
}
checks += await ClassroomChecks.Run();
checks += await ClassroomGroupChecks.Run();

// UpdateChecker behavioral checks
Check(SemVersion.TryParse("0.1.0-beta.2", out var liteV1) && liteV1.Major == 0 && liteV1.Minor == 1 && liteV1.Patch == 0 && liteV1.Prerelease == "beta.2", "NekoChat SemVersion prerelease");
Check(SemVersion.Parse("0.1.0-beta.3").CompareTo(SemVersion.Parse("0.1.0-beta.2")) > 0, "beta.3 newer than beta.2");
Check(SemVersion.Parse("0.1.0").CompareTo(SemVersion.Parse("0.1.0-beta.3")) > 0, "0.1.0 release newer than prerelease");
var sampleReleaseJson = """
[
  {
    "tag_name": "v0.1.0-beta.4",
    "name": "Neighbor Beta 4",
    "draft": false
  },
  {
    "tag_name": "nekochat-lite-v0.1.0-beta.3",
    "name": "NekoChat Lite Beta 3",
    "draft": false,
    "html_url": "https://example.com/lite-b3",
    "assets": [{"name": "NekoChat-Setup-win-x64.exe", "browser_download_url": "https://example.com/dl.exe"}]
  }
]
""";
using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sampleReleaseJson))) {
    var liteRes = UpdateChecker.ParseReleases(stream, SemVersion.Parse("0.1.0-beta.2"), "nekochat-lite-v");
    Check(liteRes.Success && liteRes.HasUpdate && liteRes.LatestVersion == "0.1.0-beta.3", "Lite detected update");
    Check(liteRes.DownloadUrl == "https://example.com/dl.exe", "Lite download url extracted");
}
using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sampleReleaseJson))) {
    var noRes = UpdateChecker.ParseReleases(stream, SemVersion.Parse("0.1.0-beta.3"), "nekochat-lite-v");
    Check(noRes.Success && !noRes.HasUpdate, "Lite no update when current");
}
Reject(() => DirectLanMessaging.SendDirectAsync("8.8.8.8", "bad").GetAwaiter().GetResult(), "public IP rejected");
Reject(() => DirectLanMessaging.SendDirectAsync("127.0.0.1", "   ").GetAwaiter().GetResult(), "empty message rejected");
checks += await DirectMessagingChecks.Run();
checks += await ReactionChecks.Run();
Console.WriteLine($"NekoChat core: {checks} behavior checks passed.");
