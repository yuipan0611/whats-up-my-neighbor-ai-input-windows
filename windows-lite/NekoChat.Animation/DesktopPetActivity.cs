using System;

namespace NekoChat.Animation;

public sealed record PetActivityFrame(string Pose, double X, double WalkDistance, bool Changed)
{
    public double Y { get; init; }
}

/// Native macOS activity order and smoothstep journey, without OS-specific desktop reading.
public sealed class DesktopPetActivity
{
    private readonly Random random;
    private string pose = "sitting";
    private double deadline, started, duration, from, destination, fromY, destinationY;
    private bool walking;
    private int cycle;

    public DesktopPetActivity(int seed) { random = new Random(seed); }
    public void Reset(double now, double delay = 2)
    {
        pose = "sitting"; walking = false; cycle = 0; deadline = now + delay;
    }

    public PetActivityFrame Step(double now, double x, double minimum, double maximum) =>
        Step(now, x, 0, minimum, maximum, 0, 0);

    public PetActivityFrame Step(double now, double x, double y, double minimum, double maximum,
        double minimumY, double maximumY, double speedScale = 1)
    {
        maximum = Math.Max(minimum, maximum);
        maximumY = Math.Max(minimumY, maximumY);
        x = Math.Max(minimum, Math.Min(maximum, x));
        y = Math.Max(minimumY, Math.Min(maximumY, y));
        if (walking)
        {
            double progress = Math.Max(0, Math.Min(1, (now - started) / duration));
            double eased = progress * progress * (3 - 2 * progress);
            double next = Math.Max(minimum, Math.Min(maximum, from + (destination - from) * eased));
            double nextY = Math.Max(minimumY, Math.Min(maximumY, fromY + (destinationY - fromY) * eased));
            double travel = Distance(next - from, nextY - fromY);
            if (progress < 1) return new(pose, next, travel, false) { Y = nextY };
            walking = false;
            pose = cycle % 3 == 0 ? "curious" : "standing";
            deadline = now + 2.2;
            return new(pose, next, travel, true) { Y = nextY };
        }
        if (now < deadline) return new(pose, x, 0, false) { Y = y };
        switch (pose)
        {
            case "sitting": return Play("standing", 1.8);
            case "standing" when cycle == 0:
            case "sleeping":
                cycle++;
                from = x;
                fromY = y;
                destination = minimum + random.NextDouble() * (maximum - minimum);
                destinationY = minimumY + random.NextDouble() * (maximumY - minimumY);
                // Visit all four corners in a bounded number of uninterrupted journeys.
                if (cycle % 3 == 0)
                {
                    int corner = (cycle / 3 - 1) % 4;
                    destination = (corner & 1) == 0 ? minimum : maximum;
                    destinationY = (corner & 2) == 0 ? minimumY : maximumY;
                }
                // Avoid a nearly stationary journey on a wide desktop.
                if (cycle % 3 != 0 && Distance(destination - x, destinationY - y) <
                    Math.Min(60, Distance(maximum - minimum, maximumY - minimumY) / 4))
                {
                    destination = x <= (minimum + maximum) / 2 ? maximum : minimum;
                    destinationY = y <= (minimumY + maximumY) / 2 ? maximumY : minimumY;
                }
                duration = Math.Max(2, Math.Min(9, Distance(destination - from, destinationY - fromY) / (70 * Math.Max(.01, speedScale))));
                started = now; walking = true;
                pose = destination < from ? "walk-left" : "walk-right";
                return new(pose, x, 0, true) { Y = y };
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
            return new(pose, x, 0, true) { Y = y };
        }
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}
