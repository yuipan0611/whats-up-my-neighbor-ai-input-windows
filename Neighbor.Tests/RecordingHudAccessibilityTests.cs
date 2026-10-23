using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Neighbor.Core;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    // Expected names, roles, and states are written out as literal values from the accessibility specification.
    // Focus behavior cannot be checked without a visible window, so it is left to manual acceptance.
    public static class RecordingHudAccessibilityTests
    {
        private const string LeftName = "取消這次錄音或辨識";
        private const string FallbackRightName = "開始或完成錄音";

        private static readonly List<EyeRow> Rows = new List<EyeRow>
        {
            new EyeRow(DictationState.Idle, "開始錄音", true, false),
            new EyeRow(DictationState.Starting, FallbackRightName, false, true),
            new EyeRow(DictationState.Recording, "完成錄音", false, false),
            new EyeRow(DictationState.Finalizing, FallbackRightName, false, true),
            new EyeRow(DictationState.Transcribing, FallbackRightName, false, true),
            new EyeRow(DictationState.Delivering, FallbackRightName, true, true),
            new EyeRow(DictationState.RetainedAudio, "重試辨識", false, false),
        };

        public static void Run(Action<bool, string> check)
        {
            StructureChecks(check);
            NameChecks(check);
            StateChecks(check);
            BoundsChecks(check);
            DefaultActionChecks(check);
            DisposedChecks(check);
            BackgroundThreadCheck(check);
            MsaaChecks(check);
        }

        private static void StructureChecks(Action<bool, string> check)
        {
            using (var hud = Fresh(DictationState.Idle))
            {
                Expect(check, "Accessibility: the HUD exposes exactly two children (left and right eyes)",
                    () => hud.AccessibleRoot.GetChildCount() == 2);
                Expect(check, "Accessibility: the left child is a RecordingHudEyeAccessibleObject",
                    () => Eye(hud, 0) != null);
                Expect(check, "Accessibility: the right child is a RecordingHudEyeAccessibleObject",
                    () => Eye(hud, 1) != null);
                Expect(check, "Accessibility: the left eye role is PushButton",
                    () => Eye(hud, 0).Role == AccessibleRole.PushButton);
                Expect(check, "Accessibility: the right eye role is PushButton",
                    () => Eye(hud, 1).Role == AccessibleRole.PushButton);
                Expect(check, "Accessibility: the left eye parent is the HUD container",
                    () => Eye(hud, 0).Parent == hud.AccessibleRoot);
                Expect(check, "Accessibility: the right eye parent is the HUD container",
                    () => Eye(hud, 1).Parent == hud.AccessibleRoot);
            }
        }

        private static void NameChecks(Action<bool, string> check)
        {
            foreach (var row in Rows)
            {
                var current = row;
                Expect(check, current.State + ": left eye name is 取消這次錄音或辨識",
                    () => { using (var hud = Fresh(current.State)) return Eye(hud, 0).Name == LeftName; });
                Expect(check, current.State + ": right eye name is " + current.RightName,
                    () => { using (var hud = Fresh(current.State)) return Eye(hud, 1).Name == current.RightName; });
            }
        }

        private static void StateChecks(Action<bool, string> check)
        {
            foreach (var row in Rows)
            {
                var current = row;
                Expect(check, current.State + ": left eye is " + (current.LeftUnavailable ? "Unavailable" : "available"),
                    () => { using (var hud = Fresh(current.State)) return IsUnavailable(Eye(hud, 0)) == current.LeftUnavailable; });
                Expect(check, current.State + ": right eye is " + (current.RightUnavailable ? "Unavailable" : "available"),
                    () => { using (var hud = Fresh(current.State)) return IsUnavailable(Eye(hud, 1)) == current.RightUnavailable; });
            }
        }

        private static void BoundsChecks(Action<bool, string> check)
        {
            using (var hud = Fresh(DictationState.Recording))
            {
                Expect(check, "Accessibility: left eye bounds are the screen rectangle of CancelEye",
                    () => Eye(hud, 0).Bounds == hud.RectangleToScreen(RecordingHudLayout.CancelEye(hud.ClientSize)));
                Expect(check, "Accessibility: right eye bounds are the screen rectangle of FinishEye",
                    () => Eye(hud, 1).Bounds == hud.RectangleToScreen(RecordingHudLayout.FinishEye(hud.ClientSize)));

                Rectangle leftBefore = BoundsOf(hud, 0), rightBefore = BoundsOf(hud, 1);
                hud.Location = new Point(hud.Location.X + 30, hud.Location.Y + 20);
                Expect(check, "Accessibility: left eye bounds move with the HUD location",
                    () => Eye(hud, 0).Bounds.X - leftBefore.X == 30 && Eye(hud, 0).Bounds.Y - leftBefore.Y == 20);
                Expect(check, "Accessibility: right eye bounds move with the HUD location",
                    () => Eye(hud, 1).Bounds.X - rightBefore.X == 30 && Eye(hud, 1).Bounds.Y - rightBefore.Y == 20);
            }
        }

        private static void DefaultActionChecks(Action<bool, string> check)
        {
            // Hidden HUDs act on nothing; the positive mapping is covered by the Activate tests, and visible
            // activation through a real default action is a manual acceptance item.
            Expect(check, "hidden: default action raises nothing (Recording left eye)",
                () => Is(DefaultActionOutcome(DictationState.Recording, RecordingHudTarget.Cancel), 0, 0, 0, 0));
            Expect(check, "hidden: default action raises nothing (Idle right eye)",
                () => Is(DefaultActionOutcome(DictationState.Idle, RecordingHudTarget.Finish), 0, 0, 0, 0));
            Expect(check, "hidden: default action raises nothing (RetainedAudio right eye)",
                () => Is(DefaultActionOutcome(DictationState.RetainedAudio, RecordingHudTarget.Finish), 0, 0, 0, 0));
            Expect(check, "Default action: Delivering left eye raises nothing",
                () => Is(DefaultActionOutcome(DictationState.Delivering, RecordingHudTarget.Cancel), 0, 0, 0, 0));
            Expect(check, "Default action: Delivering right eye raises nothing",
                () => Is(DefaultActionOutcome(DictationState.Delivering, RecordingHudTarget.Finish), 0, 0, 0, 0));

            // A hidden HUD must not act on an accessibility default action.
            using (var hud = Fresh(DictationState.Recording))
            {
                int cancels = 0;
                hud.CancelRequested += () => cancels++;
                var eye = Eye(hud, 0);
                Expect(check, "Default action: hidden HUD does not raise CancelRequested",
                    () => { eye.DoDefaultAction(); return cancels == 0; });
            }
        }

        private static void DisposedChecks(Action<bool, string> check)
        {
            var hud = Fresh(DictationState.Recording);
            int total = 0;
            hud.CancelRequested += () => total++;
            hud.FinishRequested += () => total++;
            hud.StartRequested += () => total++;
            hud.RetryRequested += () => total++;
            var left = Eye(hud, 0);
            var right = Eye(hud, 1);
            hud.Dispose();
            Expect(check, "After Dispose: left eye DoDefaultAction does not throw or raise events",
                () => { left.DoDefaultAction(); return total == 0; });
            Expect(check, "After Dispose: right eye DoDefaultAction does not throw or raise events",
                () => { right.DoDefaultAction(); return total == 0; });
        }

        // A hidden HUD called from a worker thread: the call marshals to the UI thread, which must keep pumping
        // messages for the worker to finish. Completion within the deadline therefore shows the marshalled call ran.
        // A visible HUD is needed to observe the action itself, which is a manual acceptance item.
        private static void BackgroundThreadCheck(Action<bool, string> check)
        {
            using (var hud = Fresh(DictationState.Recording))
            {
                int cancels = 0;
                hud.CancelRequested += () => cancels++;
                Exception workerError = null;
                var done = new ManualResetEvent(false);
                var worker = new Thread(() =>
                {
                    try { hud.DoAccessibleAction(RecordingHudTarget.Cancel); }
                    catch (Exception error) { workerError = error; }
                    finally { done.Set(); }
                });
                worker.IsBackground = true;
                worker.Start();

                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (!done.WaitOne(0) && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
                bool finished = done.WaitOne(0);
                check(finished, "hidden: background DoAccessibleAction completes within 2 seconds (marshal to UI thread does not deadlock)");
                check(workerError == null, "hidden: background DoAccessibleAction throws no exception");
                check(cancels == 0, "hidden: background DoAccessibleAction raises nothing");
            }
        }

        // MSAA bridge: the same client-object path that legacy assistive clients use, read through late-bound COM calls.
        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object accessible);

        private const uint OBJID_CLIENT = unchecked((uint)-4);
        private const string IidAccessible = "618736E0-3C3D-11CF-810C-00AA00389B71";
        private const int ROLE_SYSTEM_PUSHBUTTON = 43;
        private const int STATE_SYSTEM_UNAVAILABLE = 0x1;

        private static void MsaaChecks(Action<bool, string> check)
        {
            using (var hud = Fresh(DictationState.Idle))
            {
                Expect(check, "MSAA: client object reports two children", () => Convert.ToInt32(Get(Msaa(hud), "accChildCount")) == 2);
                Expect(check, "MSAA: left child role is PushButton (43)", () => Convert.ToInt32(Get(Msaa(hud), "accRole", 1)) == ROLE_SYSTEM_PUSHBUTTON);
                Expect(check, "MSAA: right child role is PushButton (43)", () => Convert.ToInt32(Get(Msaa(hud), "accRole", 2)) == ROLE_SYSTEM_PUSHBUTTON);
            }
            foreach (var row in Rows)
            {
                var current = row;
                Expect(check, current.State + ": MSAA left name is 取消這次錄音或辨識",
                    () => { using (var hud = Fresh(current.State)) return Convert.ToString(Get(Msaa(hud), "accName", 1)) == LeftName; });
                Expect(check, current.State + ": MSAA right name is " + current.RightName,
                    () => { using (var hud = Fresh(current.State)) return Convert.ToString(Get(Msaa(hud), "accName", 2)) == current.RightName; });
                Expect(check, current.State + ": MSAA left eye is " + (current.LeftUnavailable ? "Unavailable" : "available"),
                    () => { using (var hud = Fresh(current.State)) return IsUnavailableMsaa(Msaa(hud), 1) == current.LeftUnavailable; });
                Expect(check, current.State + ": MSAA right eye is " + (current.RightUnavailable ? "Unavailable" : "available"),
                    () => { using (var hud = Fresh(current.State)) return IsUnavailableMsaa(Msaa(hud), 2) == current.RightUnavailable; });
            }
            using (var hud = Fresh(DictationState.Recording))
            {
                Expect(check, "MSAA: left eye location is the screen rectangle of CancelEye",
                    () => MsaaLocation(Msaa(hud), 1) == hud.RectangleToScreen(RecordingHudLayout.CancelEye(hud.ClientSize)));
                Expect(check, "MSAA: right eye location is the screen rectangle of FinishEye",
                    () => MsaaLocation(Msaa(hud), 2) == hud.RectangleToScreen(RecordingHudLayout.FinishEye(hud.ClientSize)));
            }

            // A hidden HUD must neither throw nor act through MSAA default actions.
            using (var hud = Fresh(DictationState.Recording))
            {
                int cancels = 0;
                hud.CancelRequested += () => cancels++;
                Expect(check, "MSAA: hidden HUD accDoDefaultAction on the left eye throws nothing and raises no CancelRequested",
                    () => { DoDefaultMsaa(Msaa(hud), 1); return cancels == 0; });
            }
            using (var hud = Fresh(DictationState.Idle))
            {
                int starts = 0;
                hud.StartRequested += () => starts++;
                Expect(check, "MSAA: hidden HUD accDoDefaultAction on the right eye throws nothing and raises no StartRequested",
                    () => { DoDefaultMsaa(Msaa(hud), 2); return starts == 0; });
            }
        }

        // Obtains the MSAA client object through the WM_GETOBJECT path.
        private static object Msaa(RecordingHud hud)
        {
            var iid = new Guid(IidAccessible);
            object accessible;
            int hr = AccessibleObjectFromWindow(hud.Handle, OBJID_CLIENT, ref iid, out accessible);
            if (hr != 0 || accessible == null) throw new InvalidOperationException("AccessibleObjectFromWindow failed: 0x" + hr.ToString("X8"));
            return accessible;
        }

        private static object Get(object accessible, string property, params object[] childId)
        {
            return accessible.GetType().InvokeMember(property, BindingFlags.GetProperty, null, accessible, childId);
        }

        private static void DoDefaultMsaa(object accessible, int childId)
        {
            accessible.GetType().InvokeMember("accDoDefaultAction", BindingFlags.InvokeMethod, null, accessible, new object[] { childId });
        }

        private static Rectangle MsaaLocation(object accessible, int childId)
        {
            // accLocation(out left, out top, out width, out height, varChild): the first four are by-reference outputs.
            var args = new object[] { 0, 0, 0, 0, childId };
            var modifiers = new ParameterModifier(5);
            for (int i = 0; i < 4; i++) modifiers[i] = true;
            accessible.GetType().InvokeMember("accLocation", BindingFlags.InvokeMethod, null, accessible, args, new[] { modifiers }, null, null);
            return new Rectangle(Convert.ToInt32(args[0]), Convert.ToInt32(args[1]), Convert.ToInt32(args[2]), Convert.ToInt32(args[3]));
        }

        private static bool IsUnavailableMsaa(object accessible, int childId)
        {
            return (Convert.ToInt32(Get(accessible, "accState", childId)) & STATE_SYSTEM_UNAVAILABLE) != 0;
        }

        private static RecordingHud Fresh(DictationState state)
        {
            var hud = new RecordingHud();
            hud.UpdateState(state, "測試");
            var handle = hud.Handle; // Creates the handle without showing the window.
            return hud;
        }

        private static RecordingHudEyeAccessibleObject Eye(RecordingHud hud, int index)
        {
            return hud.AccessibleRoot.GetChild(index) as RecordingHudEyeAccessibleObject;
        }

        // A missing child reads as an empty rectangle so the dependent check fails instead of aborting the suite.
        private static Rectangle BoundsOf(RecordingHud hud, int index)
        {
            var eye = Eye(hud, index);
            return eye == null ? Rectangle.Empty : eye.Bounds;
        }

        private static bool IsUnavailable(AccessibleObject eye)
        {
            return (eye.State & AccessibleStates.Unavailable) == AccessibleStates.Unavailable;
        }

        // Activates an eye through its accessibility default action on a fresh, hidden HUD and records what it raised.
        private static Outcome DefaultActionOutcome(DictationState state, RecordingHudTarget target)
        {
            using (var hud = Fresh(state))
            {
                var outcome = new Outcome();
                hud.StartRequested += () => outcome.Start++;
                hud.FinishRequested += () => outcome.Finish++;
                hud.CancelRequested += () => outcome.Cancel++;
                hud.RetryRequested += () => outcome.Retry++;
                hud.DoAccessibleAction(target);
                return outcome;
            }
        }

        private static bool Is(Outcome outcome, int start, int finish, int cancel, int retry)
        {
            return outcome.Start == start && outcome.Finish == finish && outcome.Cancel == cancel && outcome.Retry == retry;
        }

        // Any exception counts as a failed check, so an unfinished member cannot crash the suite.
        private static void Expect(Action<bool, string> check, string name, Func<bool> assertion)
        {
            bool passed;
            try { passed = assertion(); }
            catch (Exception) { passed = false; }
            check(passed, name);
        }

        private sealed class EyeRow
        {
            public readonly DictationState State;
            public readonly string RightName;
            public readonly bool LeftUnavailable, RightUnavailable;
            public EyeRow(DictationState state, string rightName, bool leftUnavailable, bool rightUnavailable)
            {
                State = state; RightName = rightName; LeftUnavailable = leftUnavailable; RightUnavailable = rightUnavailable;
            }
        }

        private sealed class Outcome
        {
            public int Start, Finish, Cancel, Retry;
        }
    }
}
