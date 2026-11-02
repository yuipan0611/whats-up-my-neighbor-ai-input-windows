using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Presence;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class PresenceChecks
{
    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly PresenceWriteResult Written = new(true, "written");
    public static int RunAbaProbe()
    {
        int checks = 0;
        foreach (string change in new[] { "revoke-restore", "endpoint-restore", "binding-restore", "local-identity-restore" })
        {
            using var f = new DbFixture(); using var store = ChatStore.Open(f.Options); var setup = Setup(store, f); var controller = setup.Controller; var target = setup.Target;
            var initial = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, f.KeyHash)).Write!;
            var initialResult = store.ApplyRosterPresenceEvidence(initial, () => controller.IsCurrent(initial));
            if (!controller.CompletePersistence(initial, initialResult)) throw new Exception("ABA fixture did not establish initial verified presence.");
            controller.UpdateTargets(store.GetPresenceTargets()); f.Clock.Advance(15);
            var job = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, f.KeyHash)).Write!;
            string previousSeen = Text(f.Db, "SELECT lastSeenUtc FROM Peer"), previousInstall = Text(f.Db, "SELECT installEvidenceUtc FROM Peer");
            using var other = ChatStore.Open(f.Options);
            switch (change)
            {
                case "revoke-restore": other.SetPairingState(f.PeerId, PairingState.Revoked); other.SetPairingState(f.PeerId, PairingState.Paired); break;
                case "endpoint-restore":
                    other.SaveManualRosterEntry(target.RowId, new("Name", "1", false, "10.0.0.8"), Token(other));
                    other.SaveManualRosterEntry(target.RowId, new("Name", "1", false, "10.0.0.1"), Token(other)); break;
                case "binding-restore": other.BindPairedRosterPeer(target.RowId, null, Token(other)); other.BindPairedRosterPeer(target.RowId, f.PeerId, Token(other)); break;
                case "local-identity-restore":
                    using (var sql = new SqliteConnection("Data Source=" + f.Db + ";Pooling=False"))
                    { sql.Open(); using var cmd = sql.CreateCommand(); cmd.CommandText = "UPDATE LocalIdentity SET id='Temporary'; UPDATE LocalIdentity SET id='Local'"; cmd.ExecuteNonQuery(); }
                    break;
            }
            var result = store.ApplyRosterPresenceEvidence(job, () => controller.IsCurrent(job));
            Console.WriteLine($"Presence ABA {change}: {result.Reason}");
            checks++;
            if (result.Written || result.Reason != "stale-target" || controller.CompletePersistence(job, result) || Text(f.Db, "SELECT lastSeenUtc FROM Peer") != previousSeen ||
                Text(f.Db, "SELECT installEvidenceUtc FROM Peer") != previousInstall)
                throw new Exception("Presence ABA must reject the old job after " + change + "; observed " + result.Reason);
            controller.UpdateTargets(store.GetPresenceTargets());
            if (controller.GetSnapshot(target.RowId).Presence != PresenceStatus.Unknown) throw new Exception("Revision refresh must invalidate old Online after " + change);
            var fresh = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, f.KeyHash)).Write!;
            var accepted = store.ApplyRosterPresenceEvidence(fresh, () => controller.IsCurrent(fresh));
            if (!controller.CompletePersistence(fresh, accepted)) throw new Exception("Fresh revision proof must recover after " + change);
            DateTime? liveSeen = controller.GetSnapshot(target.RowId).LastSeenUtc; controller.UpdateTargets(store.GetPresenceTargets());
            if (controller.GetSnapshot(target.RowId).LastSeenUtc != liveSeen) throw new Exception("Throttled DB snapshot cannot rewind live seen time.");
        }
        return checks;
    }
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Presence #{checks}: {name}"); }
        var clock = new Clock(); var target = Target(0) with { RestoredInstalled = InstalledStatus.ConfirmedNotInstalled };
        var c = Ready(clock, [target], target.RowId);
        Check(c.GetSnapshot(target.RowId) is { Presence: PresenceStatus.Unknown, Installed: InstalledStatus.ConfirmedNotInstalled }, "stored note is separate from unknown live presence");
        var probe = c.TakeDueProbes().Start.Single(); var job = c.CompleteProbe(probe, Proof(target)).Write!;
        Check(c.GetSnapshot(target.RowId).Presence == PresenceStatus.Unknown && c.IsCurrent(job), "verified proof awaits data-layer acceptance");
        Check(!c.CompletePersistence(job, new(false, "unknown-or-untrusted-peer")) && c.GetSnapshot(target.RowId).Presence == PresenceStatus.Unknown, "DB rejected trust never produces Online");
        Check(!c.CompletePersistence(job, Written), "rejected evidence cannot be relabeled successful by a later callback");
        job = c.ObservePassive(target.RowId, target.PeerId!, Key, "10.0.0.1").Write!;
        Check(c.CompletePersistence(job, Written) && c.GetSnapshot(target.RowId) is { Installed: InstalledStatus.ConfirmedInstalled, Presence: PresenceStatus.Online }, "actual verified evidence overrides private not-installed note after DB acceptance");
        clock.Wall = clock.Wall.AddYears(10); Check(c.GetSnapshot(target.RowId).Presence == PresenceStatus.Online, "wallclock forward never expires presence");
        clock.Wall = clock.Wall.AddYears(-20); Check(c.GetSnapshot(target.RowId).Presence == PresenceStatus.Online, "wallclock backward never freezes or changes TTL");
        clock.Advance(15); c.FailProbe(c.TakeDueProbes().Start.Single());
        Check(c.GetSnapshot(target.RowId).Presence == PresenceStatus.Online, "single failure preserves prior verified Online");
        clock.Advance(29.999); Check(c.GetSnapshot(target.RowId).Presence == PresenceStatus.Online, "current target remains online before45sec");
        clock.Advance(.001); Check(c.GetSnapshot(target.RowId) is { Presence: PresenceStatus.Offline, Installed: InstalledStatus.ConfirmedInstalled }, "exact45sec expires presence but not install record");
        c.ResetAfterResume(); Check(c.GetSnapshot(target.RowId).Presence == PresenceStatus.Unknown, "resume resets live state to Unknown");
        var restarted = Ready(clock, [target with { RestoredInstalled = InstalledStatus.ConfirmedInstalled }], target.RowId);
        Check(restarted.GetSnapshot(target.RowId) is { Installed: InstalledStatus.ConfirmedInstalled, Presence: PresenceStatus.Unknown }, "restart keeps installed, never stale Online");

        clock = new(); var a = Target(0); var b = Target(1); c = Ready(clock, [a, b], a.RowId, 0);
        foreach (var p in c.TakeDueProbes().Start) c.CompletePersistence(c.CompleteProbe(p, Proof(p.Target)).Write!, Written);
        clock.Advance(45); Check(c.GetSnapshot(a.RowId).Presence == PresenceStatus.Offline && c.GetSnapshot(b.RowId).Presence == PresenceStatus.Online, "current45 and visible90 independent TTLs");
        clock.Advance(45); Check(c.GetSnapshot(b.RowId).Presence == PresenceStatus.Offline, "exact90sec visible expiry");

        foreach (PairingState? state in Enum.GetValues<PairingState>().Select(s => (PairingState?)s).Append(null))
        {
            clock = new(); var t = Target(0) with { Pairing = state, KeySha256 = state is null ? null : Key }; c = Ready(clock, [t], t.RowId);
            var d = c.CompleteProbe(c.TakeDueProbes().Start.Single(), new(true, t.PeerId!, state == PairingState.Paired ? Key : OtherKey));
            Check(state == PairingState.Paired ? d.Write is not null : d is { Accepted: true, Write: null, Reason: "endpoint-responds" } &&
                c.GetSnapshot(t.RowId) is { Installed: InstalledStatus.Unknown, Presence: PresenceStatus.Unknown, EndpointResponds: true }, "only paired+matching pin can request E2; all other states remain E1");
        }
        clock = new(); var unbound = new PresenceTarget("unbound", null, null, null, "10.0.0.1", null, "Local"); c = Ready(clock, [unbound], unbound.RowId);
        Check(c.CompleteProbe(c.TakeDueProbes().Start.Single(), new(true, "Other", Key)) is { Accepted: true, Write: null } && c.GetSnapshot(unbound.RowId).Presence == PresenceStatus.Unknown, "unbound verified endpoint never becomes a person online");
        clock.Advance(45); Check(c.GetSnapshot(unbound.RowId) is { EndpointResponds: false, Installed: InstalledStatus.Unknown, Presence: PresenceStatus.Unknown }, "E1 expires without inventing installed or Offline");
        clock = new(); var missingPin = a with { KeySha256 = null }; c = Ready(clock, [missingPin], a.RowId);
        Check(c.CompleteProbe(c.TakeDueProbes().Start.Single(), new(true, a.PeerId!, Key)).Write is null && c.GetSnapshot(a.RowId).Presence == PresenceStatus.Unknown, "paired flag without pinned key cannot create E2");
        clock = new(); c = Ready(clock, [a], a.RowId);
        Check(!c.CompleteProbe(c.TakeDueProbes().Start.Single(), new(false, a.PeerId!, Key)).Accepted && !c.GetSnapshot(a.RowId).EndpointResponds, "unsigned or unverified hello is no evidence");
        clock = new(); c = Ready(clock, [a], a.RowId);
        Check(c.CompleteProbe(c.TakeDueProbes().Start.Single(), new(true, a.PeerId!, OtherKey)).Reason == "key-mismatch" &&
            c.GetSnapshot(a.RowId) is { AddressConflict: true, ProbeSuppressed: true, EndpointResponds: false, Presence: PresenceStatus.Unknown }, "paired pin mismatch is no E1 and suppresses automatic probing");
        clock.Advance(200); Check(c.TakeDueProbes().Start.Count == 0, "key mismatch does not automatically retry");
        clock = new(); var pending = a with { Pairing = PairingState.Pending }; c = Ready(clock, [pending], a.RowId);
        Check(c.CompleteProbe(c.TakeDueProbes().Start.Single(), new(true, "Other", OtherKey)) is { Reason: "address-conflict", Write: null } && c.GetSnapshot(a.RowId).Presence == PresenceStatus.Unknown, "nonpaired changed receiver is only untrusted address response");
        foreach (bool failure in new[] { false, true })
        {
            clock = new(); c = Ready(clock, [a], a.RowId); probe = c.TakeDueProbes().Start.Single(); clock.Advance(2);
            var d = failure ? c.FailProbe(probe, ProbeFailure.KeyMismatch) : c.CompleteProbe(probe, Proof(a));
            Check(d.Reason == "expired" && c.GetSnapshot(a.RowId) is { EndpointResponds: false, AddressConflict: false, ProbeSuppressed: false }, "exact2sec rejects every result including failure hints");
        }
        foreach (var changed in new[] { a with { PeerId = "Other" }, a with { KeySha256 = OtherKey }, a with { Pairing = PairingState.Revoked },
            a with { DeclaredEndpoint = "10.0.0.9" }, a with { ObservedEndpoint = "10.0.0.8" }, a with { LocalIdentityId = "NewLocal" } })
        {
            clock = new(); c = Ready(clock, [a], a.RowId); probe = c.TakeDueProbes().Start.Single(); var canceled = c.UpdateTargets([changed]);
            Check(canceled.Contains(probe.Id) && !c.CompleteProbe(probe, Proof(a)).Accepted && c.GetSnapshot(a.RowId).Presence == PresenceStatus.Unknown, "all security/route/localidentity target changes fence late completion");
        }
        clock = new(); c = Ready(clock, [a], a.RowId); probe = c.TakeDueProbes().Start.Single(); c.UpdateTargets([]); c.UpdateTargets([a]);
        Check(c.TakeDueProbes().Start.Single().Id != probe.Id && !c.CompleteProbe(probe, Proof(a)).Accepted, "remove/readd cannot reuse generation or probe id");
        clock = new(); c = Ready(clock, [a, b], a.RowId, 0); var switching = c.TakeDueProbes();
        Check(c.SetView(true, [a.RowId, b.RowId], b.RowId).Count == 2 && !c.CompleteProbe(switching.Start[0], Proof(a)).Accepted && c.TakeDueProbes().Start[0].Target.RowId == b.RowId,
            "switching current target fences even still-visible old probes and prioritizes new target");
        clock = new(); c = Ready(clock, [a], a.RowId); probe = c.TakeDueProbes().Start.Single();
        Check(c.SetView(false, [], a.RowId).Contains(probe.Id) && !c.CompleteProbe(probe, Proof(a)).Accepted && c.TakeDueProbes().Start.Count == 0, "hidden window cancels and ignores late response");
        var passive = c.ObservePassive(a.RowId, a.PeerId!, Key, "10.0.0.1").Write!;
        Check(c.CompletePersistence(passive, Written) && c.GetSnapshot(a.RowId).Presence == PresenceStatus.Online && c.TakeDueProbes().Start.Count == 0, "verified passive evidence works hidden without starting probes");
        c.SetView(true, [], a.RowId); Check(c.TakeDueProbes().Start.Single().Target.RowId == a.RowId, "selected offscreen target stays eligible");
        clock = new(); c = Ready(clock, [a], a.RowId); var olderJob = c.ObservePassive(a.RowId, a.PeerId!, Key, "10.0.0.1").Write!;
        clock.Advance(1); var newerJob = c.ObservePassive(a.RowId, a.PeerId!, Key, "10.0.0.1").Write!;
        Check(c.CompletePersistence(newerJob, Written) && !c.CompletePersistence(olderJob, Written) && !c.IsCurrent(olderJob) && c.GetSnapshot(a.RowId).LastSeenUtc == newerJob.ObservedUtc,
            "out-of-order persistence cannot regress newer evidence");
        var unrelated = Ready(clock, [a], a.RowId);
        Check(!unrelated.IsCurrent(newerJob), "matching row and counter values from another controller do not confer job ownership");
        c.UpdateTargets([a with { Pairing = PairingState.Revoked }]);
        Check(c.GetSnapshot(a.RowId) is { Presence: PresenceStatus.Unknown, Installed: InstalledStatus.ConfirmedInstalled }, "trust downgrade removes Online while preserving historical install record");

        clock = new(); var many = Enumerable.Range(0, 8).Select(Target).ToArray(); c = Ready(clock, many, many[^1].RowId, 0);
        var batch = c.TakeDueProbes();
        Check(batch.Start.Count == 4 && batch.Start[0].Target.RowId == many[^1].RowId && batch.Start.Select(p => p.Target.RowId).Distinct().Count() == 4 && c.TakeDueProbes().Start.Count == 0,
            "four in-flight cap, selected priority and overlap dedup");
        var pendingJobs = batch.Start.Select(p => c.CompleteProbe(p, Proof(p.Target)).Write!).ToArray();
        Check(pendingJobs.All(c.IsCurrent) && c.TakeDueProbes().Start.Count == 0 && !c.CompleteProbe(batch.Start[0], Proof(batch.Start[0].Target)).Accepted,
            "awaiting storage consumes the bounded probe slots and duplicate completion cannot enqueue another job");
        clock.Advance(2); var next = c.TakeDueProbes();
        Check(next.Cancel.Count == 4 && next.Start.Count == 4 && pendingJobs.All(j => !c.IsCurrent(j)) && !next.Start.Any(p => batch.Start.Any(old => old.Target.RowId == p.Target.RowId)), "expired tickets canceled before bounded new batch");
        clock = new(); c = Ready(clock, [a, b], a.RowId); c.FailProbe(c.TakeDueProbes().Start.Single());
        clock.Advance(4.999); Check(c.TakeDueProbes().Start.Count == 0, "startup staggering waits injected delay");
        clock.Advance(.001); Check(c.TakeDueProbes().Start.Single().Target.RowId == b.RowId, "startup noncurrent stagger bounded0to10seconds");
        foreach (double random in new[] { 0d, 1d })
        {
            clock = new(); c = Ready(clock, [a], a.RowId, random); c.FailProbe(c.TakeDueProbes().Start.Single());
            clock.Advance((random == 0 ? 12 : 18) - .001); Check(c.TakeDueProbes().Start.Count == 0, "period jitter lower/upper boundary before due");
            clock.Advance(.001); Check(c.TakeDueProbes().Start.Count == 1, "period jitter exact12or18seconds");
            clock = new(); c = Ready(clock, [a], null, random); if (random == 1) clock.Advance(10);
            c.FailProbe(c.TakeDueProbes().Start.Single()); clock.Advance((random == 0 ? 24 : 36) - .001);
            Check(c.TakeDueProbes().Start.Count == 0, "visible-row period jitter lower/upper boundary before due");
            clock.Advance(.001); Check(c.TakeDueProbes().Start.Count == 1, "visible-row jitter exact24or36seconds");
        }
        clock = new(); c = Ready(clock, [a], a.RowId);
        for (int i = 0; i < 3; i++) { c.FailProbe(c.TakeDueProbes().Start.Single()); if (i < 2) clock.Advance(15); }
        clock.Advance(119.999); Check(c.TakeDueProbes().Start.Count == 0, "three failures back off120sec");
        clock.Advance(.001); var recovered = c.CompleteProbe(c.TakeDueProbes().Start.Single(), Proof(a));
        Check(recovered.Write is not null && c.GetSnapshot(a.RowId).Failures == 0, "fresh real proof resets backoff");
        bool accepted = c.CompletePersistence(recovered.Write!, Written); clock.Advance(14.999); bool early = c.TakeDueProbes().Start.Count == 0; clock.Advance(.001);
        Check(accepted && early && c.TakeDueProbes().Start.Count == 1, "after real evidence normal15sec scheduling resumes");

        clock = new(); c = Ready(clock, [a], a.RowId); probe = c.TakeDueProbes().Start.Single(); using (var gate = new ManualResetEventSlim())
        {
            var controller = c; var ticket = probe;
            var work = new[] { Task.Factory.StartNew(() => { gate.Wait(TimeSpan.FromSeconds(8)); var j = controller.ObservePassive(a.RowId, a.PeerId!, Key, "10.0.0.1").Write!; controller.CompletePersistence(j, Written); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
                Task.Factory.StartNew(() => { gate.Wait(TimeSpan.FromSeconds(8)); controller.FailProbe(ticket); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default) };
            gate.Set(); await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(12));
            Check(c.GetSnapshot(a.RowId).Presence == PresenceStatus.Online, "concurrent probe failure cannot erase verified passive evidence");
        }

        using (var f = new DbFixture())
        {
            using var store = ChatStore.Open(f.Options); var setup = Setup(store, f); var controller = setup.Controller; var t = setup.Target;
            Check(t.KeySha256 == f.KeyHash && t.PeerId == f.PeerId && t.EffectiveEndpoint == "10.0.0.1", "SQLite target snapshot uses pinned public key and canonical candidate");
            var j = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, f.KeyHash)).Write!;
            var result = store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j));
            Check(result is { Written: true, Reason: "written" } && controller.CompletePersistence(j, result) && controller.GetSnapshot(t.RowId).Presence == PresenceStatus.Online,
                "guarded DB acceptance is required before Online");
            string installed = Text(f.Db, "SELECT installEvidenceUtc FROM Peer"); controller.UpdateTargets(store.GetPresenceTargets());
            Check(controller.GetSnapshot(t.RowId).Presence == PresenceStatus.Online, "own observed-endpoint/install refresh does not erase fresh evidence");
            f.Clock.Advance(1); j = controller.ObservePassive(t.RowId, f.PeerId, f.KeyHash, "10.0.0.1").Write!;
            result = store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j));
            Check(result is { Written: false, Reason: "throttled" } && controller.CompletePersistence(j, result), "throttled DB result remains accepted only after all guards");
            f.Clock.Advance(59); j = controller.ObservePassive(t.RowId, f.PeerId, f.KeyHash, "10.0.0.1").Write!;
            Check(store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j)).Written && Text(f.Db, "SELECT installEvidenceUtc FROM Peer") == installed, "exact60sec seen update preserves set-once install time");
            controller.UpdateTargets(store.GetPresenceTargets()); j = controller.ObservePassive(t.RowId, f.PeerId, f.KeyHash, "10.0.0.2").Write!;
            result = store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j)); controller.CompletePersistence(j, result); controller.UpdateTargets(store.GetPresenceTargets());
            Check(result.Written && store.GetPresenceTargets().Single().EffectiveEndpoint == "10.0.0.2" && controller.GetSnapshot(t.RowId).Presence == PresenceStatus.Online,
                "verified passive source changes observed route and remains Online after refresh");
        }
        foreach (string change in new[] { "revoke", "unbind", "endpoint", "identity", "pin" })
        {
            using var f = new DbFixture(); using var store = ChatStore.Open(f.Options); var setup = Setup(store, f); var controller = setup.Controller; var t = setup.Target;
            if (change == "pin") { controller.UpdateTargets([t with { KeySha256 = OtherKey }]); t = t with { KeySha256 = OtherKey }; }
            var j = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, t.KeySha256!)).Write!;
            switch (change)
            {
                case "revoke": store.SetPairingState(f.PeerId, PairingState.Revoked); break;
                case "unbind": store.BindPairedRosterPeer(t.RowId, null, Token(store)); break;
                case "endpoint": store.SaveManualRosterEntry(t.RowId, new("Name", "1", false, "10.0.0.8"), Token(store)); break;
                case "identity": store.ReplaceLocalIdentity("OtherLocal", null, null); break;
            }
            var result = store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j));
            Check(!result.Written && result.Reason is not "throttled" && !controller.CompletePersistence(j, result) && Text(f.Db, "SELECT installEvidence FROM Peer") == "" &&
                controller.GetSnapshot(t.RowId).Presence == PresenceStatus.Unknown, "DB rechecks changed binding/trust/route/local identity/pin even with stale controller snapshot");
        }
        foreach (string boundary in new[] { "before-write", "before-commit", "deadline", "io" })
        {
            using var f = new DbFixture(); var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); var setup = Setup(store, f); var controller = setup.Controller;
            var j = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, f.KeyHash)).Write!;
            void Change(string op)
            {
                if (op != "roster-presence") return;
                if (boundary == "io") throw new IOException("injected before commit");
                if (boundary == "deadline") f.Clock.Advance(2); else controller.SetView(false, [], setup.Target.RowId);
            }
            if (boundary == "before-write") faults.Begun = Change; else faults.Before = Change;
            PresenceWriteResult? result = null; bool failed = false; try { result = store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j)); } catch (IOException) { failed = true; }
            Check((boundary == "io" ? failed : result is { Written: false, Reason: "stale-target" }) && Text(f.Db, "SELECT lastSeenUtc FROM Peer") == "" && Text(f.Db, "SELECT installEvidence FROM Peer") == "",
                "view/deadline/fault at writer boundary or final commit fence rolls back all presence evidence");
        }
        using (var f = new DbFixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); var setup = Setup(store, f); var controller = setup.Controller;
            var j = controller.CompleteProbe(controller.TakeDueProbes().Start.Single(), new(true, f.PeerId, f.KeyHash)).Write!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            faults.Begun = op => { if (op == "roster-presence") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("presence gate"); } };
            var work = Task.Factory.StartNew(() => store.ApplyRosterPresenceEvidence(j, () => controller.IsCurrent(j)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try { if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("presence not entered"); controller.SetView(false, [], setup.Target.RowId); }
            finally { release.Set(); }
            var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result is { Written: false, Reason: "stale-target" } && Text(f.Db, "SELECT lastSeenUtc FROM Peer") == "", "view updates stay responsive while DB waits and stale transaction writes nothing");
        }
        Console.WriteLine($"Presence: {checks} evidence, monotonic TTL, scheduler, generation and guarded SQLite checks passed."); return checks;
    }

    private static PresenceTarget Target(int i) => new("row" + i, "Peer" + i, Key, PairingState.Paired, "10.0.0." + (i + 1), null, "Local");
    private static HelloPresenceProof Proof(PresenceTarget t) => new(true, t.PeerId!, t.KeySha256!);
    private static PresenceController Ready(Clock clock, PresenceTarget[] targets, string? current, double random = .5)
    {
        var controller = new PresenceController(clock, () => random); controller.UpdateTargets(targets); controller.SetView(true, targets.Select(t => t.RowId), current); return controller;
    }
    private static string Token(ChatStore s) => RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot());
    private static (PresenceController Controller, PresenceTarget Target) Setup(ChatStore store, DbFixture fixture)
    {
        store.EnsureLocalIdentity("Local"); store.RegisterPeer(fixture.PeerId, PairingState.Paired, fixture.PublicKey);
        string row = store.SaveManualRosterEntry(null, new("Name", "1", false, "10.0.0.1"), Token(store)).EntryId!; store.BindPairedRosterPeer(row, fixture.PeerId, Token(store));
        var target = store.GetPresenceTargets().Single(); return (Ready(fixture.Clock, [target], target.RowId), target);
    }
    private static string Text(string db, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal DateTimeOffset Wall = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => Wall;
        internal void Advance(double seconds) { long ticks = TimeSpan.FromSeconds(seconds).Ticks; _ticks += ticks; Wall = Wall.AddTicks(ticks); }
    }
    private sealed class DbFixture : IDisposable
    {
        internal byte[] PublicKey { get; }
        internal string PeerId { get; }
        internal readonly Clock Clock = new();
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-presence-" + Guid.NewGuid().ToString("N")); internal string Db => Path.Combine(Data, "nekochat.db");
        internal string KeyHash { get; }
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => Clock.Wall.UtcDateTime };
        internal DbFixture()
        {
            Directory.CreateDirectory(Data); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); PublicKey = key.ExportSubjectPublicKeyInfo();
            byte[] hash = SHA256.HashData(PublicKey); KeyHash = Convert.ToHexString(hash).ToLowerInvariant(); PeerId = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        public void Dispose() => Directory.Delete(Data, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, Begun;
        public void AfterBegin(string op) => Begun?.Invoke(op);
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
