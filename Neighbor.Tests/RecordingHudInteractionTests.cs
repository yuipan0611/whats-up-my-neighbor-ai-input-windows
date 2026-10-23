using System;
using System.Collections.Generic;
using Neighbor.Core;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    // Expected actions are written out as literal values from the dictation state table, not derived from the implementation.
    public static class RecordingHudInteractionTests
    {
        private static readonly List<ActionRow> ActionTable = new List<ActionRow>
        {
            new ActionRow(DictationState.Idle, RecordingHudAction.None, RecordingHudAction.Start),
            new ActionRow(DictationState.Starting, RecordingHudAction.Cancel, RecordingHudAction.None),
            new ActionRow(DictationState.Recording, RecordingHudAction.Cancel, RecordingHudAction.Finish),
            new ActionRow(DictationState.Finalizing, RecordingHudAction.Cancel, RecordingHudAction.None),
            new ActionRow(DictationState.Transcribing, RecordingHudAction.Cancel, RecordingHudAction.None),
            new ActionRow(DictationState.Delivering, RecordingHudAction.None, RecordingHudAction.None),
            new ActionRow(DictationState.RetainedAudio, RecordingHudAction.Cancel, RecordingHudAction.Retry),
        };

        public static void Run(Action<bool, string> check)
        {
            ActionForChecks(check);
            ActivationChecks(check);
        }

        private static void ActionForChecks(Action<bool, string> check)
        {
            foreach (var row in ActionTable)
            {
                var state = row.State;
                Expect(check, "ActionFor: " + state + ", left eye (cancel) → " + row.Cancel,
                    () => RecordingHudLayout.ActionFor(RecordingHudTarget.Cancel, state) == row.Cancel);
                Expect(check, "ActionFor: " + state + ", right eye (finish) → " + row.Finish,
                    () => RecordingHudLayout.ActionFor(RecordingHudTarget.Finish, state) == row.Finish);
                Expect(check, "ActionFor: " + state + ", no eye (None) → None",
                    () => RecordingHudLayout.ActionFor(RecordingHudTarget.None, state) == RecordingHudAction.None);
            }
        }

        private static void ActivationChecks(Action<bool, string> check)
        {
            Expect(check, "Recording: right eye activation raises only FinishRequested",
                () => Is(Activation(DictationState.Recording, RecordingHudTarget.Finish), true, 0, 1, 0, 0));
            Expect(check, "Idle: right eye activation raises only StartRequested",
                () => Is(Activation(DictationState.Idle, RecordingHudTarget.Finish), true, 1, 0, 0, 0));
            Expect(check, "RetainedAudio: right eye activation raises only RetryRequested, never StartRequested",
                () => Is(Activation(DictationState.RetainedAudio, RecordingHudTarget.Finish), true, 0, 0, 0, 1));
            Expect(check, "Starting: right eye activation is refused and raises nothing",
                () => Is(Activation(DictationState.Starting, RecordingHudTarget.Finish), false, 0, 0, 0, 0));
            Expect(check, "Finalizing: right eye activation is refused and raises nothing",
                () => Is(Activation(DictationState.Finalizing, RecordingHudTarget.Finish), false, 0, 0, 0, 0));
            Expect(check, "Transcribing: right eye activation is refused and raises nothing",
                () => Is(Activation(DictationState.Transcribing, RecordingHudTarget.Finish), false, 0, 0, 0, 0));

            foreach (DictationState state in new[] { DictationState.Starting, DictationState.Recording, DictationState.Finalizing, DictationState.Transcribing, DictationState.RetainedAudio })
            {
                var cancelable = state;
                Expect(check, cancelable + ": left eye activation raises only CancelRequested",
                    () => Is(Activation(cancelable, RecordingHudTarget.Cancel), true, 0, 0, 1, 0));
            }
            Expect(check, "Idle: left eye activation is refused and raises nothing",
                () => Is(Activation(DictationState.Idle, RecordingHudTarget.Cancel), false, 0, 0, 0, 0));
            Expect(check, "Delivering: left eye activation is refused and raises nothing",
                () => Is(Activation(DictationState.Delivering, RecordingHudTarget.Cancel), false, 0, 0, 0, 0));
            Expect(check, "Delivering: right eye activation is refused and raises nothing",
                () => Is(Activation(DictationState.Delivering, RecordingHudTarget.Finish), false, 0, 0, 0, 0));

            foreach (DictationState state in Enum.GetValues(typeof(DictationState)))
            {
                var current = state;
                Expect(check, current + ": no-eye target returns false and raises nothing",
                    () => Is(Activation(current, RecordingHudTarget.None), false, 0, 0, 0, 0));
            }
        }

        // Each activation is measured on a fresh form, so event counts cannot leak between cases.
        private static Outcome Activation(DictationState state, RecordingHudTarget target)
        {
            using (var hud = new RecordingHud())
            {
                var counts = new Outcome();
                hud.StartRequested += () => counts.Start++;
                hud.FinishRequested += () => counts.Finish++;
                hud.CancelRequested += () => counts.Cancel++;
                hud.RetryRequested += () => counts.Retry++;
                hud.UpdateState(state, "測試");
                counts.Returned = hud.Activate(target);
                return counts;
            }
        }

        private static bool Is(Outcome outcome, bool returned, int start, int finish, int cancel, int retry)
        {
            return outcome.Returned == returned && outcome.Start == start && outcome.Finish == finish
                && outcome.Cancel == cancel && outcome.Retry == retry;
        }

        // Any exception counts as a failed check, so an unfinished member cannot crash the suite.
        private static void Expect(Action<bool, string> check, string name, Func<bool> assertion)
        {
            bool passed;
            try { passed = assertion(); }
            catch (Exception) { passed = false; }
            check(passed, name);
        }

        private sealed class ActionRow
        {
            public readonly DictationState State;
            public readonly RecordingHudAction Cancel, Finish;
            public ActionRow(DictationState state, RecordingHudAction cancel, RecordingHudAction finish)
            {
                State = state; Cancel = cancel; Finish = finish;
            }
        }

        private sealed class Outcome
        {
            public bool Returned;
            public int Start, Finish, Cancel, Retry;
        }
    }
}
