using System.Windows;

namespace NekoChat.Windows;

// Desktop rectangles use physical pixels, including negative monitor coordinates.
internal static class PetDesktopLayout
{
    internal static double NormalizeScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : 1;

    internal static Rect Clamp(Rect pet, Rect work) => new(
        Math.Clamp(pet.Left, work.Left, Math.Max(work.Left, work.Right - pet.Width)),
        Math.Clamp(pet.Top, work.Top, Math.Max(work.Top, work.Bottom - pet.Height)),
        pet.Width, pet.Height);

    internal static Rect Visible(Rect frame, Rect normalized) => new(
        frame.Left + normalized.Left * frame.Width, frame.Top + normalized.Top * frame.Height,
        normalized.Width * frame.Width, normalized.Height * frame.Height);

    internal static Rect ClampArtwork(Rect frame, Rect normalized, Rect work)
    {
        var visible = Visible(frame, normalized);
        var clamped = Clamp(visible, work);
        frame.Offset(clamped.Left - visible.Left, clamped.Top - visible.Top);
        return frame;
    }

    internal static Rect PlaceBubble(Rect pet, Size desired, Rect work, double gap)
    {
        double width = Math.Min(desired.Width, work.Width);
        double height = Math.Min(desired.Height, work.Height);
        double top = pet.Top - gap - height;
        if (top < work.Top) top = pet.Bottom + gap;
        return Clamp(new Rect(pet.Left + (pet.Width - width) / 2, top, width, height), work);
    }
}
