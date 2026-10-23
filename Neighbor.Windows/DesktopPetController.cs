using System;

namespace Neighbor.Windows
{
    // The part of the companion window that the controller needs. Tests replace it with a fake.
    public interface IDesktopPetView : IDisposable
    {
        event Action<Exception> Failed;
        void Configure(bool visible, bool isBusy);
        void Hide();
    }

    // Owns the optional desktop companion: creation, enabled and busy state, failure handling and release.
    // Runs on the UI thread only. A failure stops the companion, is recorded once and notified once;
    // dictation is never affected, and the user's saved preference is never changed.
    public sealed class DesktopPetController : IDisposable
    {
        public const string CreateStage = "desktop-pet-create";
        public const string RunStage = "desktop-pet-run";
        private const string CreateMessage = "無法建立桌面小黑貓，聽寫功能不受影響。詳細原因見 diagnostics.log。";
        private const string RunMessage = "桌面小黑貓已暫停，聽寫功能不受影響。詳細原因見 diagnostics.log。";
        private readonly Func<IDesktopPetView> factory;
        private readonly Action<string, Exception> record;
        private readonly Action<string> notify;
        private IDesktopPetView view;
        private bool disposed;

        public DesktopPetController(Func<IDesktopPetView> factory, Action<string, Exception> record, Action<string> notify)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.record = record ?? throw new ArgumentNullException(nameof(record));
            this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
        }

        // The user's preference for this session. Failures never change it, so the saved setting stays intact.
        public bool Enabled { get; private set; }
        public bool Busy { get; private set; }
        public bool Failed { get; private set; }
        public bool Created { get { return view != null; } }

        // Creates the view hidden; nothing is shown until SetEnabled(true).
        public void Create()
        {
            if (disposed || view != null || Failed) return;
            try
            {
                view = factory();
                view.Failed += OnViewFailed;
            }
            catch (Exception error) { Fail(CreateStage, error, CreateMessage); }
        }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            Apply();
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;
            Apply();
        }

        // Used when the application exits; the view stays alive until Dispose.
        public void Hide()
        {
            if (disposed || view == null) return;
            view.Hide();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (view == null) return;
            view.Failed -= OnViewFailed;
            view.Dispose();
            view = null;
        }

        private void Apply()
        {
            if (disposed || Failed || view == null) return;
            view.Configure(Enabled, Busy);
        }

        private void OnViewFailed(Exception error)
        {
            Fail(RunStage, error, RunMessage);
        }

        private void Fail(string stage, Exception error, string message)
        {
            if (Failed) return;
            Failed = true;
            if (view != null)
            {
                view.Failed -= OnViewFailed;
                view.Hide();
            }
            try { record(stage, error); }
            catch { /* Logging must not turn one failure into a second one. */ }
            notify(message);
        }
    }
}
