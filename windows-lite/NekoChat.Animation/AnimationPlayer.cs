using System;
using System.Collections.Generic;
using System.IO;
#if !NETFRAMEWORK
using System.Text.Json;
#endif

namespace NekoChat.Animation;

public sealed record Clip(double Duration, int Count, bool Loop);
public sealed record Manifest(int Version, double[] Times, int[] Sources, int CarryIndex,
    string[] Coats, string[] CoatNames, Dictionary<string, Clip> Clips)
{
#if !NETFRAMEWORK
    public static Manifest Load(string path) => JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("缺少動畫資料。");
#endif
}

/// Pure frame playback: no settings, input injection, chat, credentials or network access.
public sealed class AnimationPlayer(Manifest manifest)
{
    public string Mode { get; private set; } = "idle";
    public string? ClipName { get; private set; }
    public int LiftIndex { get; private set; }
    public bool Dragging { get; private set; }
    public bool Pressed { get; private set; }
    public bool Paused { get; private set; }
    public const double StrideDistance = 86 * 0.28 / 0.6;
    private double? walkDistance;
    private double started, clock, pausedAt;
    private int startIndex, endIndex;
    private string pressMode = "idle";

    public static int Travel(double[] times, int start, int end, double elapsed)
    {
        var target = times[start] + (end >= start ? 1 : -1) * Math.Max(0, elapsed);
        var result = start;
        if (end >= start) { for (int i = start; i <= end; i++) if (times[i] <= target + 1e-7) result = i; }
        else { for (int i = start; i >= end; i--) if (times[i] >= target - 1e-7) result = i; }
        return result;
    }

    public void Tick(double now)
    {
        if (Paused || Pressed && !Dragging) return;
        clock = now;
        if (Mode is "lifting" or "landing") {
            LiftIndex = Travel(manifest.Times, startIndex, endIndex, now - started);
            if (now - started >= Math.Abs(manifest.Times[endIndex] - manifest.Times[startIndex]) - 1e-7) {
                LiftIndex = endIndex;
                Mode = Mode == "lifting" ? "carried" : "idle";
            }
        } else if (Mode == "clip" && ClipName is { } name && !manifest.Clips[name].Loop && now - started >= manifest.Clips[name].Duration) {
            Mode = "idle"; LiftIndex = manifest.Sources.Length - 1;
        }
    }

    public string Frame {
        get {
            if (Mode != "clip" || ClipName is null) return $"lift-{manifest.Sources[LiftIndex]:D2}.png";
            var clip = manifest.Clips[ClipName];
            var phase = Math.Max(0, clock - started);
            var frameClip = ClipName;
            if (ClipName is "walk-left" or "walk-right" && phase < clip.Duration) {
                frameClip += "-start";
            }
            if (ClipName is "walk-left" or "walk-right" && walkDistance is { } distance)
            {
                frameClip = ClipName + (distance < StrideDistance ? "-start" : "");
                var walkedFraction = distance % StrideDistance / StrideDistance;
                return $"{frameClip}-{Math.Min(clip.Count - 1, (int)(walkedFraction * clip.Count)):D3}.png";
            }
            var fraction = clip.Loop ? phase % clip.Duration / clip.Duration : Math.Min(1, phase / clip.Duration);
            var index = Math.Min(clip.Count - 1, (int)(fraction * (clip.Loop ? clip.Count : Math.Max(1, clip.Count - 1))));
            return $"{frameClip}-{index:D3}.png";
        }
    }

    public void SetWalkDistance(double distance)
    {
        if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance));
        if (!Paused && Mode == "clip" && ClipName is "walk-left" or "walk-right") walkDistance = distance;
    }

    public void Play(string name, double now)
    {
        if (!manifest.Clips.ContainsKey(name)) throw new ArgumentException("未知動作。", nameof(name));
        Reset(now); Mode = "clip"; ClipName = name;
    }
    public void Reset(double now)
    {
        walkDistance = null;
        Pressed = Dragging = Paused = false; Mode = "idle"; ClipName = null;
        LiftIndex = 0; started = clock = now;
    }
    public void Pause(bool pause, double now)
    {
        if (Pressed) return;
        if (pause && !Paused) { Tick(now); pausedAt = now; Paused = true; }
        else if (!pause && Paused) { started += now - pausedAt; clock += now - pausedAt; Paused = false; }
    }
    public void Press(double now)
    {
        Pause(false, now); Tick(now); pressMode = Mode;
        if (Mode == "clip") LiftIndex = 0;
        Pressed = true; Dragging = false;
    }
    public void Drag(double now)
    {
        if (!Pressed || Dragging) return;
        Dragging = true; startIndex = LiftIndex; endIndex = manifest.CarryIndex;
        Mode = "lifting"; started = clock = now;
    }
    public void Release(double now)
    {
        if (!Pressed) return;
        Tick(now); var landing = Dragging || pressMode == "landing";
        Pressed = Dragging = false;
        if (!landing) { Play("petted", now); return; }
        startIndex = LiftIndex;
        endIndex = LiftIndex >= manifest.CarryIndex ? manifest.Sources.Length - 1 : 0;
        Mode = "landing"; started = clock = now;
        Tick(now);
    }
    public void Cancel(double now) => Release(now);
}
