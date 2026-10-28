using System;

namespace NekoChat.Animation;

public sealed record PetActivityFrame(string Pose, double X, double WalkDistance, bool Changed);

/// Native macOS activity order and smoothstep journey, without OS-specific desktop reading.
public sealed class DesktopPetActivity
{
    private readonly Random random;
    private string pose = "sitting";
    private double deadline, started, duration, from, destination;
    private bool walking;
    private int cycle;

    public DesktopPetActivity(int seed) { random = new Random(seed); }
    public void Reset(double now, double delay = 2)
    {
        pose = "sitting"; walking = false; cycle = 0; deadline = now + delay;
    }

    public PetActivityFrame Step(double now, double x, double minimum, double maximum)
    {
        maximum = Math.Max(minimum, maximum);
        x = Math.Max(minimum, Math.Min(maximum, x));
        if (walking)
        {
            double progress = Math.Max(0, Math.Min(1, (now - started) / duration));
            double eased = progress * progress * (3 - 2 * progress);
            double next = Math.Max(minimum, Math.Min(maximum, from + (destination - from) * eased));
            if (progress < 1) return new(pose, next, Math.Abs(next - from), false);
            walking = false;
            pose = cycle % 3 == 0 ? "curious" : "standing";
            deadline = now + 2.2;
            return new(pose, next, 0, true);
        }
        if (now < deadline) return new(pose, x, 0, false);
        switch (pose)
        {
            case "sitting": return Play("standing", 1.8);
            case "standing" when cycle == 0:
            case "sleeping":
                cycle++;
                from = x;
                destination = minimum + random.NextDouble() * (maximum - minimum);
                // Avoid a nearly stationary journey on a wide desktop.
                if (Math.Abs(destination - x) < Math.Min(60, (maximum - minimum) / 4))
                    destination = x <= (minimum + maximum) / 2 ? maximum : minimum;
                duration = Math.Max(2, Math.Min(9, Math.Abs(destination - from) / 70));
                started = now; walking = true;
                pose = destination < from ? "walk-left" : "walk-right";
                return new(pose, x, 0, true);
            case "standing":
            case "curious": return Play("grooming", 3.2);
            case "grooming": return Play("yawning", 3.2);
            case "yawning": return Play("loafing", 5);
            case "loafing": return Play("sleeping", 18 + random.NextDouble() * 16);
            default: return Play("sitting", 2);
        }

        PetActivityFrame Play(string next, double seconds)
        {
            pose = next; deadline = now + seconds;
            return new(pose, x, 0, true);
        }
    }
}
