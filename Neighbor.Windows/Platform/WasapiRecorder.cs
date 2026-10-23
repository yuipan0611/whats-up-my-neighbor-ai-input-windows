using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Neighbor.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Neighbor.Windows.Platform
{
    public sealed class WasapiRecorder : IAudioRecorder
    {
        private sealed class Session
        {
            public readonly object Sync = new object();
            public WasapiCapture Capture;
            public MMDevice Device;
            public WaveFileWriter Writer;
            public CaptureFormatConverter Converter;
            public readonly byte[] Scratch = new byte[16000];
            public CancellationTokenRegistration Cancellation;
            public readonly TaskCompletionSource<bool> Stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> FirstData = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool StopRequested, Finished;
        }
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private Session current;
        private bool disposed;
        public event EventHandler<AudioLevelEventArgs> LevelChanged;
        public event EventHandler StoppedUnexpectedly;

        public IReadOnlyList<AudioDevice> GetDevices()
        {
            var result = new List<AudioDevice>();
            using (var enumerator = new MMDeviceEnumerator())
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    using (device) result.Add(new AudioDevice { Id = device.ID, Name = device.FriendlyName });
            }
            return result;
        }

        public async Task StartAsync(string path, string deviceId, CancellationToken cancellation)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(WasapiRecorder));
                if (current != null && !current.Stopped.Task.IsCompleted) throw new InvalidOperationException("錄音尚未結束。");
                // Construct on the pool so NAudio does not capture the WinForms sync context.
                // RecordingStopped can then always complete while the UI is shutting down.
                var started = await Task.Run(() =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var session = new Session();
                    try
                    {
                        using (var enumerator = new MMDeviceEnumerator())
                            session.Device = string.IsNullOrWhiteSpace(deviceId)
                                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                                : enumerator.GetDevice(deviceId);
                        // Use the device's mix format; forcing 16 kHz is refused by many microphones.
                        session.Capture = new WasapiCapture(session.Device, false, 50);
                        session.Converter = new CaptureFormatConverter(session.Capture.WaveFormat);
                        session.Writer = new WaveFileWriter(path, CaptureFormatConverter.Target);
                        session.Capture.DataAvailable += (sender, args) => DataAvailable(session, args);
                        session.Capture.RecordingStopped += (sender, args) => Finish(session, args.Exception);
                        current = session;
                        session.Capture.StartRecording();
                        var registration = cancellation.Register(() => RequestStop(session));
                        bool alreadyFinished;
                        lock (session.Sync)
                        {
                            alreadyFinished = session.Finished;
                            if (!alreadyFinished) session.Cancellation = registration;
                        }
                        if (alreadyFinished) registration.Dispose();
                        return session;
                    }
                    catch
                    {
                        // StartRecording may fail before its worker exists. Dispose is safe here;
                        // once started, only the stopped callback owns writer/device disposal.
                        if (session.Capture != null) session.Capture.Dispose();
                        session.Writer?.Dispose(); session.Device?.Dispose();
                        session.Stopped.TrySetResult(true);
                        if (ReferenceEquals(current, session)) current = null;
                        throw;
                    }
                }).ConfigureAwait(false);
                // NAudio starts its capture thread asynchronously. Do not report recording
                // ready until a real packet (including digital silence) reached the writer.
                var ready = await Task.WhenAny(started.FirstData.Task, started.Stopped.Task, Task.Delay(3000)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (ready != started.FirstData.Task || started.Stopped.Task.IsCompleted)
                {
                    RequestStop(started);
                    await Task.WhenAny(started.Stopped.Task, Task.Delay(5000)).ConfigureAwait(false);
                    throw new InvalidOperationException("麥克風未能開始傳送音訊。請檢查 Windows 麥克風權限與選取的裝置。");
                }
            }
            finally { gate.Release(); }
        }

        private void DataAvailable(Session session, WaveInEventArgs args)
        {
            double squares = 0;
            int samples = 0;
            lock (session.Sync)
            {
                if (session.Finished) return;
                session.Converter.Write(args.Buffer, 0, args.BytesRecorded);
                int read;
                while ((read = session.Converter.Read(session.Scratch, 0, session.Scratch.Length)) > 0)
                {
                    session.Writer.Write(session.Scratch, 0, read);
                    session.FirstData.TrySetResult(true);
                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        double sample = (short)(session.Scratch[i] | session.Scratch[i + 1] << 8) / 32768d;
                        squares += sample * sample;
                    }
                    samples += read / 2;
                }
            }
            if (samples > 0)
            {
                double rms = Math.Sqrt(squares / samples);
                LevelChanged?.Invoke(this, new AudioLevelEventArgs { Decibels = rms > 0 ? Math.Max(-96, 20 * Math.Log10(rms)) : -96, IsKnown = true });
            }
        }
        private static void RequestStop(Session session)
        {
            lock (session.Sync)
            {
                if (session.Finished || session.StopRequested) return;
                session.StopRequested = true;
                session.Capture.StopRecording();
            }
        }
        private void Finish(Session session, Exception error)
        {
            bool unexpected;
            lock (session.Sync)
            {
                if (session.Finished) return;
                session.Finished = true;
                unexpected = error != null || !session.StopRequested;
            }
            // Cleanup off the callback; cancellation disposal may wait for its callback.
            Task.Run(() =>
            {
                var failures = new List<Exception>();
                if (error != null) failures.Add(error);
                Cleanup(() => session.Cancellation.Dispose(), failures);
                Cleanup(() => session.Writer?.Dispose(), failures);
                Cleanup(() => session.Capture?.Dispose(), failures);
                Cleanup(() => session.Device?.Dispose(), failures);
                unexpected = unexpected || failures.Count > 0;
                if (unexpected)
                {
                    if (failures.Count == 0) failures.Add(new InvalidOperationException("麥克風意外停止錄音。"));
                    // Publish interruption before allowing StopAsync/new StartAsync to
                    // continue, so an old failure cannot be mistaken for the next job.
                    Cleanup(() => StoppedUnexpectedly?.Invoke(this, EventArgs.Empty), failures);
                }
                if (failures.Count == 0) session.Stopped.TrySetResult(true);
                else if (failures.Count == 1) session.Stopped.TrySetException(failures[0]);
                else session.Stopped.TrySetException(new AggregateException("錄音結束時發生錯誤。", failures));
            });
        }
        private static void Cleanup(Action action, List<Exception> failures)
        { try { action(); } catch (Exception error) { failures.Add(error); } }

        public async Task StopAsync()
        {
            Session session;
            await gate.WaitAsync().ConfigureAwait(false);
            try { session = current; if (session != null) RequestStop(session); }
            finally { gate.Release(); }
            if (session != null)
            {
                if (await Task.WhenAny(session.Stopped.Task, Task.Delay(5000)).ConfigureAwait(false) != session.Stopped.Task)
                    throw new TimeoutException("麥克風未能正常結束錄音；請關閉程式後重新開啟。");
                await session.Stopped.Task.ConfigureAwait(false);
            }
        }
        public void Dispose()
        {
            gate.Wait();
            try { if (disposed) return; disposed = true; }
            finally { gate.Release(); }
            // Do not dispose native audio objects while callbacks are still active. If
            // a driver never reports RecordingStopped, process exit releases them.
            // StopAsync and StoppedUnexpectedly already surface recording failures;
            // disposal must still let the owner release its other resources.
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
