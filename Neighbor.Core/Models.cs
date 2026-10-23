using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Neighbor.Core
{
    public enum OutputMode { Verbatim, Standard, Concise }
    public enum DictationState { Idle, Starting, Recording, Finalizing, Transcribing, Delivering, RetainedAudio }
    public sealed class Shortcut
    {
        public int Modifiers { get; set; } = 3; // MOD_ALT | MOD_CONTROL
        public int Key { get; set; } = 0x20;
        public Shortcut Clone() { return new Shortcut { Modifiers = Modifiers, Key = Key }; }
    }
    public sealed class AppSettings
    {
        public string PrimaryLanguage { get; set; } = "zh";
        public List<string> AdditionalLanguages { get; set; } = new List<string> { "en" };
        public OutputMode Mode { get; set; } = OutputMode.Verbatim;
        public bool ConvertChineseNumerals { get; set; }
        public bool AutomaticLanguageRetry { get; set; } = true;
        public int SilenceSeconds { get; set; }
        public Shortcut PrimaryShortcut { get; set; } = new Shortcut();
        public Shortcut AlternateShortcut { get; set; }
        public OutputMode AlternateMode { get; set; } = OutputMode.Concise;
        public string InputDeviceId { get; set; }
        public bool HudAtTop { get; set; }
        public bool LaunchAtLogin { get; set; }
        public bool IntroductionShown { get; set; }
        public bool DesktopPetEnabled { get; set; } // Off by default, matching macOS first-run behavior.
        public AppSettings Clone()
        {
            return new AppSettings {
                PrimaryLanguage = PrimaryLanguage, AdditionalLanguages = new List<string>(AdditionalLanguages ?? new List<string>()),
                Mode = Mode, ConvertChineseNumerals = ConvertChineseNumerals, AutomaticLanguageRetry = AutomaticLanguageRetry,
                SilenceSeconds = SilenceSeconds, PrimaryShortcut = PrimaryShortcut?.Clone(), AlternateShortcut = AlternateShortcut?.Clone(),
                AlternateMode = AlternateMode, InputDeviceId = InputDeviceId, HudAtTop = HudAtTop,
                LaunchAtLogin = LaunchAtLogin, IntroductionShown = IntroductionShown, DesktopPetEnabled = DesktopPetEnabled
            };
        }
    }
    public sealed class AudioDevice { public string Id { get; set; } public string Name { get; set; } public override string ToString() { return Name; } }
    public sealed class AudioLevelEventArgs : EventArgs
    {
        public double Decibels { get; set; }
        public bool IsKnown { get; set; }
    }
    public interface IAudioRecorder : IDisposable
    {
        event EventHandler<AudioLevelEventArgs> LevelChanged;
        event EventHandler StoppedUnexpectedly;
        IReadOnlyList<AudioDevice> GetDevices();
        Task StartAsync(string path, string deviceId, CancellationToken cancellation);
        Task StopAsync();
    }
    public interface ICredentialStore { string Read(); void Write(string key); void Delete(); }
    public interface ITranscriber
    {
        Task<string> TranscribeAsync(string path, string language, string prompt, CancellationToken cancellation);
    }
    public sealed class FocusSnapshot
    {
        public object Native { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public bool CanPaste { get; set; }
        public string Reason { get; set; }
    }
    public sealed class InsertionResult
    {
        public bool Copied { get; set; }
        public bool PasteSent { get; set; }
        public bool PasteVerified { get; set; }
        public string Message { get; set; }
    }
    public sealed class CorrectionEventArgs : EventArgs
    {
        public string Original { get; set; }
        public string Corrected { get; set; }
        public bool IsValid { get; set; }
        public string Reason { get; set; }
    }
    public interface ITextDelivery : IDisposable
    {
        event EventHandler<CorrectionEventArgs> CorrectionObserved;
        Task<FocusSnapshot> CaptureAsync();
        Task<InsertionResult> DeliverAsync(string text, FocusSnapshot target, CancellationToken cancellation);
        Task<bool> RevalidateCorrectionAsync();
        void StopObserving();
    }
    public interface ITextNormalizer { string ToTraditional(string text); }
    public sealed class TextSpan { public int Start { get; set; } public int Length { get; set; } }
    public interface IWordSegmenter { IReadOnlyList<TextSpan> GetWords(string text); }
    public sealed class VocabularyEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Term { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Pinned { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
    public sealed class VocabularyCandidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Original { get; set; }
        public string Corrected { get; set; }
        public string Status { get; set; } = "等待確認";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
    public sealed class VocabularyPrompt
    {
        public string Text { get; set; }
        public List<string> IncludedIds { get; set; } = new List<string>();
        public List<string> ExcludedIds { get; set; } = new List<string>();
    }
    public sealed class StatusEventArgs : EventArgs
    {
        public DictationState State { get; set; }
        public string Message { get; set; }
    }
}
