using System;
using System.Collections.Generic;
using Neighbor.Windows;

namespace Neighbor.Tests
{
    // The controller is tested with a fake view, so no real window is created.
    public static class DesktopPetControllerTests
    {
        private sealed class FakeView : IDesktopPetView
        {
            public event Action<Exception> Failed;
            public int ConfigureCalls, DisposeCalls;
            public bool LastVisible, LastBusy;
            public void Configure(bool visible, bool isBusy) { ConfigureCalls++; LastVisible = visible; LastBusy = isBusy; }
            public void Hide() { }
            public void Dispose() { DisposeCalls++; }
            public void Raise(Exception error) { Failed?.Invoke(error); }
        }

        private sealed class Harness
        {
            public readonly List<string> Notes = new List<string>();
            public readonly List<string> Records = new List<string>();
            public FakeView View = new FakeView();
            public Func<IDesktopPetView> Factory;
            public Harness() { Factory = () => View; }
            public DesktopPetController Create()
            {
                return new DesktopPetController(() => Factory(), (stage, error) => Records.Add(stage), message => Notes.Add(message));
            }
        }

        public static void Run(Action<bool, string> check)
        {
            CreationFailureIsContained(check);
            RuntimeFailureNotifiesOnce(check);
            StateReachesTheView(check);
            PreferenceSurvivesFailure(check);
            ReleaseIsIdempotent(check);
            FailureAfterReleaseIsIgnored(check);
        }

        private static void CreationFailureIsContained(Action<bool, string> check)
        {
            var harness = new Harness { Factory = () => throw new InvalidOperationException("synthetic creation failure") };
            var controller = harness.Create();
            bool threw = false;
            try { controller.Create(); } catch { threw = true; }
            check(!threw && controller.Failed, "Creation failure is contained and marks the companion as failed");
            check(harness.Notes.Count == 1, "Creation failure notifies exactly once");
            check(harness.Records.Count == 1 && harness.Records[0] == "desktop-pet-create", "Creation failure is recorded with its stage");
            controller.SetEnabled(true);
            controller.SetBusy(true);
            controller.Create();
            check(harness.Notes.Count == 1, "Later state changes after a failed creation do not notify again");
        }

        private static void RuntimeFailureNotifiesOnce(Action<bool, string> check)
        {
            var harness = new Harness();
            var controller = harness.Create();
            controller.Create();
            controller.SetEnabled(true);
            int before = harness.View.ConfigureCalls;
            harness.View.Raise(new InvalidOperationException("synthetic run failure"));
            harness.View.Raise(new InvalidOperationException("second failure"));
            check(controller.Failed, "Runtime failure marks the companion as failed");
            check(harness.Notes.Count == 1, "Repeated runtime failures notify only once");
            check(harness.Records.Count == 1 && harness.Records[0] == "desktop-pet-run", "Runtime failure is recorded once, with the run stage");
            controller.SetEnabled(false);
            controller.SetBusy(false);
            check(harness.View.ConfigureCalls == before, "After a failure the view receives no further state changes");
        }

        private static void StateReachesTheView(Action<bool, string> check)
        {
            var harness = new Harness();
            var controller = harness.Create();
            controller.Create();
            check(harness.View.ConfigureCalls == 0, "Creating the companion does not show or configure it");
            controller.SetEnabled(true);
            check(harness.View.LastVisible && !harness.View.LastBusy, "Enabling passes visible=true and busy=false");
            controller.SetBusy(true);
            check(harness.View.LastVisible && harness.View.LastBusy, "Busy state reaches the view while enabled");
            controller.SetEnabled(false);
            check(!harness.View.LastVisible && harness.View.LastBusy, "Disabling passes visible=false and keeps the busy state");
            check(!controller.Enabled, "Enabled reflects the last requested preference");
        }

        private static void PreferenceSurvivesFailure(Action<bool, string> check)
        {
            var harness = new Harness();
            var controller = harness.Create();
            controller.Create();
            controller.SetEnabled(true);
            harness.View.Raise(new InvalidOperationException("synthetic run failure"));
            check(controller.Enabled, "A runtime failure does not change the user's enabled preference");

            var broken = new Harness { Factory = () => throw new InvalidOperationException("synthetic creation failure") };
            var brokenController = broken.Create();
            brokenController.Create();
            brokenController.SetEnabled(true);
            check(brokenController.Enabled, "A creation failure does not change the user's enabled preference");
        }

        private static void ReleaseIsIdempotent(Action<bool, string> check)
        {
            var harness = new Harness();
            var controller = harness.Create();
            controller.Create();
            controller.Dispose();
            controller.Dispose();
            bool threw = false;
            try { controller.Hide(); controller.SetEnabled(true); controller.SetBusy(true); } catch { threw = true; }
            check(harness.View.DisposeCalls == 1, "Releasing twice disposes the view exactly once");
            check(!threw && harness.View.ConfigureCalls == 0, "Calls after release are ignored without throwing");
        }

        private static void FailureAfterReleaseIsIgnored(Action<bool, string> check)
        {
            var harness = new Harness();
            var controller = harness.Create();
            controller.Create();
            controller.Dispose();
            harness.View.Raise(new InvalidOperationException("late failure"));
            check(harness.Notes.Count == 0 && harness.Records.Count == 0, "A failure raised after release is not notified or recorded");
        }
    }
}
