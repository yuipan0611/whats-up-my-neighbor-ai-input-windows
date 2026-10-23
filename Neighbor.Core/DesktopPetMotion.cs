using System;

namespace Neighbor.Core
{
    /// <summary>Small, deterministic first-stage walking model. Coordinates are screen pixels.</summary>
    public sealed class DesktopPetMotion
    {
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Distance { get; private set; }
        public bool FacingLeft { get; private set; }
        public bool Walking { get; private set; }
        public bool Dragging { get; private set; }
        private double rest = 2, journey;

        private const double Sprite = 128; // Window size is Sprite * scale.
        private double grabX, grabY, areaLeft, areaRight;
        private bool moved;

        // Puts the cat at 65% of the walkable span and on the floor of the work area.
        public void PlaceInitially(double left, double top, double right, double bottom, double scale)
        {
            double maxX = Math.Max(left, right - Sprite * scale);
            X = left + (maxX - left) * .65;
            Y = bottom - Sprite * scale;
        }

        // Keeps the offset between the cat and the pointer from the moment the drag begins.
        public void BeginDrag(double pointerX, double pointerY)
        {
            grabX = X - pointerX; grabY = Y - pointerY;
            Dragging = true; Walking = false; moved = false;
        }

        public void DragTo(double pointerX, double pointerY, double left, double top, double right, double bottom, double scale)
        {
            if (!Dragging) return;
            areaLeft = left; areaRight = right; moved = true;
            X = pointerX + grabX; Y = pointerY + grabY;
            Fit(left, top, right, bottom, scale);
        }

        // Releasing keeps the height and starts a horizontal walk from zero distance.
        // Without a move there is no work area to judge from, so the current facing is kept.
        public void EndDrag()
        {
            if (!Dragging) return;
            Dragging = false; Walking = true; journey = 0; Distance = 0;
            if (moved) FacingLeft = X > (areaLeft + areaRight) / 2;
        }

        public void CancelDrag()
        {
            if (!Dragging) return;
            Dragging = false; rest = 2;
        }

        // Clamps both axes into the full work area, so a shrunk or removed screen pulls the cat back in.
        public void Fit(double left, double top, double right, double bottom, double scale)
        {
            double size = Sprite * scale;
            X = Clamp(X, left, right - size);
            Y = Clamp(Y, top, bottom - size);
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(Math.Max(min, max), value));

        public void Advance(double seconds, double left, double top, double right, double bottom, double scale, bool paused)
        {
            if (Dragging) return;
            Fit(left, top, right, bottom, scale);
            if (paused || seconds <= 0 || scale <= 0) return;
            seconds = Math.Min(seconds, .25); // No jump after sleep or a blocked UI thread.
            double maxX = Math.Max(left, right - Sprite * scale);
            if (!Walking)
            {
                rest -= seconds;
                if (rest > 0 || maxX - left < scale) return;
                Walking = true; journey = 0; Distance = 0;
                FacingLeft = X > (left + maxX) / 2;
            }
            double step = 55 * scale * seconds;
            double next = Math.Max(left, Math.Min(maxX, X + (FacingLeft ? -step : step)));
            Distance += Math.Abs(next - X) / scale;
            X = next; journey += seconds;
            if (X <= left || X >= maxX || journey >= 7)
            { Walking = false; rest = 3; }
        }

        // Ported from macOS DesktopPetView.pawOffset; alternate diagonal pairs.
        public static void PawOffset(double distance, int pair, out float x, out float y)
        {
            const double stride = 86 * .28 / .6;
            distance = Math.Max(0, distance);
            double cycle = (distance / stride + .3 + pair * .5) % 1;
            if (cycle < .6) { x = (float)(-43 + 86 * cycle / .6); y = 0; return; }
            double swing = (cycle - .6) / .4, eased = swing * swing * (3 - 2 * swing);
            double startup = Math.Min(1, distance / (stride * .08));
            x = (float)(43 - 86 * eased);
            y = (float)(-Math.Sin(swing * Math.PI) * 28 * startup * startup * (3 - 2 * startup));
        }
    }
}
