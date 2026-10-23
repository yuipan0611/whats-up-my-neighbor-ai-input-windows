using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Neighbor.Core;
using Neighbor.Windows.UI;

namespace Neighbor.Tests
{
    internal static class DesktopPetTests
    {
        private static DesktopPetMotion Dropped(double x, double y)
        {
            var pet = new DesktopPetMotion();
            pet.PlaceInitially(0, 0, 1920, 1080, 1);
            pet.Advance(.1, 0, 0, 1920, 1080, 1, false);
            pet.BeginDrag(pet.X, pet.Y);
            pet.DragTo(x, y, 0, 0, 1920, 1080, 1);
            pet.EndDrag();
            return pet;
        }

        private static void RunDragChecks(Action<bool, string> check)
        {
            const double left = 0, top = 0, right = 1920, bottom = 1080;

            var suspended = Dropped(600, 400);
            suspended.BeginDrag(suspended.X + 10, suspended.Y + 10);
            check(suspended.Dragging && !suspended.Walking, "Dragging suspends walking as soon as it begins");

            var grabbed = Dropped(300, 200);
            grabbed.BeginDrag(340, 250);
            bool noJump = grabbed.X == 300 && grabbed.Y == 200;
            grabbed.DragTo(440, 330, left, top, right, bottom, 1);
            check(noJump && grabbed.X == 400 && grabbed.Y == 280, "Drag keeps the grab offset instead of centering on the pointer");

            var free = Dropped(300, 200);
            free.BeginDrag(300, 200);
            free.DragTo(900, 150, left, top, right, bottom, 1);
            bool movedRight = free.X == 900 && free.Y == 150;
            free.DragTo(120, 700, left, top, right, bottom, 1);
            check(movedRight && free.X == 120 && free.Y == 700, "Drag moves freely on both X and Y");

            var released = Dropped(300, 200);
            released.BeginDrag(300, 200);
            released.DragTo(700, 500, left, top, right, bottom, 1);
            released.EndDrag();
            bool heldAtRelease = !released.Dragging && released.Y == 500;
            for (int i = 0; i < 40; i++) released.Advance(.25, left, top, right, bottom, 1, false);
            check(heldAtRelease && released.Y == 500, "Release keeps the dropped height without falling or drifting");

            var walking = Dropped(700, 500);
            double previousX = walking.X;
            bool heightFixed = true, walkedHorizontally = false;
            for (int i = 0; i < 600; i++)
            {
                walking.Advance(.1, left, top, right, bottom, 1, false);
                heightFixed &= walking.Y == 500;
                walkedHorizontally |= Math.Abs(walking.X - previousX) > .01;
                previousX = walking.X;
            }
            check(heightFixed && walkedHorizontally, "Horizontal walk after release never changes height");

            var clamped = Dropped(300, 200);
            clamped.BeginDrag(300, 200);
            clamped.DragTo(5000, -300, left, top, right, bottom, 1);
            bool clampedHighRight = clamped.X == right - 128 && clamped.Y == top;
            clamped.DragTo(-400, 2000, left, top, right, bottom, 1);
            check(clampedHighRight && clamped.X == left && clamped.Y == bottom - 128, "Drag keeps the whole 128 px cat inside the work area on both axes");

            var shrunk = Dropped(1500, 800);
            shrunk.Fit(0, 0, 1000, 600, 1);
            check(shrunk.X == 1000 - 128 && shrunk.Y == 600 - 128, "Work area shrink pulls the cat back to a visible spot");

            var removed = Dropped(1500, 800);
            removed.Fit(0, 0, 0, 0, 1);
            bool parked = removed.X == 0 && removed.Y == 0;
            removed.BeginDrag(0, 0);
            removed.DragTo(50, 60, left, top, right, bottom, 1);
            removed.EndDrag();
            check(parked && removed.X == 50 && removed.Y == 60 && removed.Walking, "Removed work area parks the cat at its origin and drag still works");

            var unchanged = Dropped(300, 200);
            unchanged.Fit(left, top, right, bottom, 1);
            check(unchanged.X == 300 && unchanged.Y == 200, "Work area change leaves an in-bounds cat where it was");

            var waiting = Dropped(300, 200);
            waiting.BeginDrag(300, 200);
            for (int i = 0; i < 40; i++) waiting.Advance(1, left, top, right, bottom, 1, false);
            check(waiting.Dragging && !waiting.Walking && waiting.Distance == 0 && waiting.X == 300, "Time during a drag adds no walking distance or rest progress");
            waiting.EndDrag();
            check(waiting.Distance == 0, "Release after a long drag starts with zero walked distance");

            var cancelled = Dropped(300, 200);
            cancelled.BeginDrag(300, 200);
            cancelled.DragTo(450, 260, left, top, right, bottom, 1);
            cancelled.CancelDrag();
            bool stoppedAtCancel = !cancelled.Dragging && cancelled.X == 450 && cancelled.Y == 260;
            bool resumed = false;
            for (int i = 0; i < 60; i++) { cancelled.Advance(.25, left, top, right, bottom, 1, false); resumed |= cancelled.Walking; }
            check(stoppedAtCancel && resumed && cancelled.Y == 260, "Cancelled drag stops in place and normal walking resumes");

            var idle = Dropped(300, 200);
            idle.CancelDrag();
            check(!idle.Dragging && idle.X == 300 && idle.Y == 200, "Cancel without an active drag changes nothing");

            var clicked = new DesktopPetMotion();
            clicked.PlaceInitially(0, 0, 428, 1080, 1);
            clicked.Advance(.1, 0, 0, 428, 1080, 1, false);
            bool leftHalf = clicked.X < 960;
            clicked.BeginDrag(clicked.X, clicked.Y);
            clicked.EndDrag();
            check(leftHalf && clicked.Walking && !clicked.FacingLeft, "Click without movement keeps the cat facing its current direction");
        }

        private static void Tick(DesktopPetMotion pet, int count, double left, double right)
        {
            // 0.25 s steps keep the 2 s rest and 7 s journey counts exact in binary floating point.
            for (int i = 0; i < count; i++) pet.Advance(.25, left, 0, right, 1080, 1, false);
        }

        private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

        private static void RunCharacterizationChecks(Action<bool, string> check)
        {
            const double left = 0, top = 0, right = 1920, bottom = 1080;

            var trace = new DesktopPetMotion();
            trace.PlaceInitially(0, 0, 1920, 1080, 1);
            Tick(trace, 1, 0, 1920);
            check(Near(trace.X, 1164.8) && !trace.Walking, "Placement puts the cat at 65% of the span and it rests");
            Tick(trace, 7, 0, 1920);
            check(trace.Walking && trace.FacingLeft && Near(trace.X, 1151.05) && Near(trace.Distance, 13.75), "First journey starts on call 8 facing left, since the cat is right of midpoint 896");
            Tick(trace, 27, 0, 1920);
            check(!trace.Walking && Near(trace.X, 779.8) && Near(trace.Distance, 385), "Journey stops after 7 s of walking: 28 steps of 13.75 px");
            Tick(trace, 12, 0, 1920);
            check(trace.Walking && !trace.FacingLeft && Near(trace.X, 793.55) && Near(trace.Distance, 13.75), "Rest lasts 3 s, then the next journey starts facing right");
            Tick(trace, 27, 0, 1920);
            check(!trace.Walking && Near(trace.X, 1164.8) && Near(trace.Distance, 385), "Second journey ends back at 1164.8 after the same 385 px");

            var timeline = new DesktopPetMotion();
            timeline.PlaceInitially(0, 0, 1920, 1080, 1);
            Tick(timeline, 7, 0, 1920);
            bool restingAt7 = !timeline.Walking;
            Tick(timeline, 1, 0, 1920);
            bool startedAt8 = timeline.Walking;
            Tick(timeline, 26, 0, 1920);
            bool stillAt34 = timeline.Walking;
            Tick(timeline, 1, 0, 1920);
            bool stoppedAt35 = !timeline.Walking;
            Tick(timeline, 11, 0, 1920);
            bool restingAt46 = !timeline.Walking;
            Tick(timeline, 1, 0, 1920);
            check(restingAt7 && startedAt8 && stillAt34 && stoppedAt35 && restingAt46 && timeline.Walking, "State timeline: rest through call 7, walk 8 to 34, rest 35 to 46, walk again from 47");

            var edge = new DesktopPetMotion();
            edge.PlaceInitially(0, 0, 228, 1080, 1);
            bool inside = true;
            for (int i = 0; i < 12; i++) { edge.Advance(.25, 0, 0, 228, 1080, 1, false); inside &= edge.X >= 0 && edge.X <= 100; }
            check(inside && Near(edge.X, 0) && !edge.Walking && Near(edge.Distance, 65), "Left edge: cat stops at x=0 after 65 px and never leaves the area");
            for (int i = 0; i < 19; i++) { edge.Advance(.25, 0, 0, 228, 1080, 1, false); inside &= edge.X >= 0 && edge.X <= 100; }
            check(inside && Near(edge.X, 100) && !edge.Walking && Near(edge.Distance, 100), "Right edge: after rest the cat stops at x=100 after 100 px and never leaves the area");

            var negative = new DesktopPetMotion();
            negative.PlaceInitially(-1920, 0, 0, 1080, 1);
            bool negativeInside = true;
            for (int i = 0; i < 35; i++) { negative.Advance(.25, -1920, 0, 0, 1080, 1, false); negativeInside &= negative.X >= -1920 && negative.X <= -128; }
            check(negativeInside && Near(negative.X, -1140.2) && !negative.Walking && negative.FacingLeft && Near(negative.Distance, 385), "Negative-coordinate monitor (left=-1920, right=-128) walks left inside bounds");

            var narrow = Dropped(300, 200);
            narrow.Fit(0, 0, 50, 50, 1);
            check(narrow.X == 0 && narrow.Y == 0, "Work area narrower than the cat clamps to its top-left corner");

            var scaled = Dropped(900, 500);
            scaled.Fit(0, 0, 1000, 600, 1.5);
            check(Near(scaled.X, 808) && Near(scaled.Y, 408), "Scale 1 to 1.5 (192 px window) clamps to right-192 and bottom-192");
            scaled.Fit(0, 0, 1000, 600, 2);
            check(Near(scaled.X, 744) && Near(scaled.Y, 344), "Scale 1.5 to 2 (256 px window) clamps to right-256 and bottom-256");

            var released = Dropped(700, 500);
            bool heightKept = true;
            for (int i = 0; i < 28; i++) { released.Advance(.25, left, top, right, bottom, 1, false); heightKept &= released.Y == 500; }
            check(heightKept && Near(released.X, 1085) && !released.Walking && !released.FacingLeft && Near(released.Distance, 385), "Release at x=700 walks right at y=500 with no vertical change");

            var leftward = Dropped(1200, 500);
            leftward.Advance(.25, left, top, right, bottom, 1, false);
            check(Near(leftward.X, 1186.25) && leftward.FacingLeft && leftward.Y == 500, "Release right of the midpoint walks left at the same height");

            var midpoint = Dropped(960, 500);
            midpoint.Advance(.25, left, top, right, bottom, 1, false);
            check(Near(midpoint.X, 973.75) && !midpoint.FacingLeft, "Release exactly on the midpoint faces right");
        }

        public static void Run(Action<bool, string> check)
        {
            var pet = new DesktopPetMotion();
            pet.PlaceInitially(-1920, 0, 0, 1080, 1);
            pet.Advance(.1, -1920, 0, 0, 1080, 1, false);
            double start = pet.X;
            for (int i = 0; i < 30; i++) pet.Advance(.1, -1920, 0, 0, 1080, 1, true);
            check(pet.X == start && !pet.Walking, "Pet suspension freezes movement and rest clock");
            for (int i = 0; i < 30; i++) pet.Advance(.1, -1920, 0, 0, 1080, 1, false);
            check(pet.Walking && pet.X < start && pet.FacingLeft, "Pet starts leftward walk on negative-coordinate monitor");
            bool bounded = true, rested = false;
            for (int i = 0; i < 2000; i++)
            {
                pet.Advance(.1, -1920, 0, 0, 1080, 1, false);
                bounded &= pet.X >= -1920 && pet.X <= -128;
                rested |= !pet.Walking;
            }
            check(bounded && rested, "Pet remains in work area and rests between journeys");
            pet.Advance(60, 0, 0, 228, 1080, 1, false);
            check(pet.X >= 0 && pet.X <= 100, "Pet clamps position after monitor removal without a sleep jump");
            pet.Advance(.1, 5, 0, 128, 1080, 1, false);
            check(pet.X == 5, "Pet handles a work area narrower than its window");
            DesktopPetMotion.PawOffset(0, 0, out float x, out float y);
            check(Math.Abs(x) < .001 && y == 0, "Walking starts with a neutral planted paw");
            DesktopPetMotion.PawOffset(20, 1, out x, out y);
            check(Math.Abs(x) <= 43 && y <= 0 && y >= -28, "Paw swing remains bounded");
            RunDragChecks(check);
            RunCharacterizationChecks(check);
            check(new AppSettings { DesktopPetEnabled = false }.Clone().DesktopPetEnabled == false, "Pet preference survives settings clone");
            check(!new AppSettings().DesktopPetEnabled, "Pet is off by default, matching the macOS first-run state");
            string directory = Path.Combine(Path.GetTempPath(), "Neighbor-pet-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new SettingsStore(Path.Combine(directory, "settings.json"));
                store.Save(new AppSettings { DesktopPetEnabled = false });
                check(!store.Load().DesktopPetEnabled, "Pet preference survives settings save and reload");
            }
            finally { Directory.Delete(directory, true); }
            using (var artwork = new DesktopPetArtwork())
            using (var image = new Bitmap(128, 128))
            using (var graphics = Graphics.FromImage(image))
            {
                int rendered = 0;
                foreach (bool left in new[] { false, true })
                    for (int i = 0; i < 16; i++)
                    {
                        graphics.Clear(Color.Transparent); artwork.Draw(graphics, 128, true, left, i * 2.5);
                        if (image.GetPixel(64, 88).A > 0) rendered++;
                    }
                graphics.Clear(Color.Transparent); artwork.Draw(graphics, 128, false, false, 0);
                check(rendered == 32 && image.GetPixel(64, 88).A > 0, "Original artwork renders sitting and 32 directional walking frames");
                check(image.GetPixel(0, 0).A == 0, "Pet artwork retains transparent background");
                string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results", "pet-preview");
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, "sitting.png"), ImageFormat.Png);
                foreach (bool left in new[] { false, true })
                {
                    using (var strip = new Bitmap(128 * 8, 128))
                    using (var canvas = Graphics.FromImage(strip))
                    {
                        canvas.Clear(Color.FromArgb(247, 246, 243));
                        for (int i = 0; i < 8; i++)
                        {
                            var state = canvas.Save(); canvas.TranslateTransform(i * 128, 0);
                            artwork.Draw(canvas, 128, true, left, i * 5); canvas.Restore(state);
                        }
                        strip.Save(Path.Combine(output, left ? "walking-left.png" : "walking-right.png"), ImageFormat.Png);
                    }
                }
            }
        }
    }
}
