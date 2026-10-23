using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace Neighbor.Windows.Platform
{
    // Microphones often refuse a 16 kHz shared-mode WASAPI stream (for example a 48 kHz
    // stereo float device), so capture runs in the device's own mix format and this
    // converter produces the 16 kHz mono 16-bit PCM that the WAV file and Groq expect.
    //
    // It only emits samples that the input already supports. Pull-style resamplers keep
    // producing zero padding after their input runs dry, which would never end a read loop.
    public sealed class CaptureFormatConverter
    {
        public static readonly WaveFormat Target = new WaveFormat(16000, 16, 1);
        // Anti-alias low-pass for the 16 kHz output (Nyquist 8 kHz) and its filter half-width.
        private const double CutoffHz = 7200;
        private const int HalfTaps = 32;

        private readonly int channels, blockAlign;
        private readonly bool isFloat;
        private readonly double inputRate, step, normalizedCutoff;
        private readonly List<float> window = new List<float>();
        private readonly MemoryStream ready = new MemoryStream();
        private int readPosition;
        private long windowStart;
        private double nextPosition;

        public CaptureFormatConverter(WaveFormat source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Channels != 1 && source.Channels != 2)
                throw new NotSupportedException("麥克風的聲道數不受支援，請選擇其他輸入裝置。");
            bool float32 = source.Encoding == WaveFormatEncoding.IeeeFloat && source.BitsPerSample == 32;
            bool pcm16 = source.Encoding == WaveFormatEncoding.Pcm && source.BitsPerSample == 16;
            if (!float32 && !pcm16)
                throw new NotSupportedException("麥克風的音訊格式不受支援，請選擇其他輸入裝置。");
            channels = source.Channels;
            blockAlign = source.BlockAlign;
            isFloat = float32;
            inputRate = source.SampleRate;
            step = inputRate / Target.SampleRate;
            normalizedCutoff = CutoffHz / inputRate;
        }

        // Accepts one capture packet in the source format.
        public void Write(byte[] buffer, int offset, int count)
        {
            int frames = count / blockAlign;
            for (int frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int at = offset + frame * blockAlign + channel * (isFloat ? 4 : 2);
                    sum += isFloat ? BitConverter.ToSingle(buffer, at) : BitConverter.ToInt16(buffer, at) / 32768d;
                }
                window.Add((float)(sum / channels));
            }
            Emit();
        }

        // Returns converted 16 kHz mono PCM. Call until it returns 0 after each Write.
        public int Read(byte[] destination, int offset, int count)
        {
            int n = Math.Min(count, (int)ready.Length - readPosition);
            if (n <= 0) return 0;
            Array.Copy(ready.GetBuffer(), readPosition, destination, offset, n);
            readPosition += n;
            if (readPosition == ready.Length) { ready.SetLength(0); readPosition = 0; }
            return n;
        }

        private void Emit()
        {
            long available = windowStart + window.Count;
            while (nextPosition + HalfTaps < available)
            {
                short value = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(Sample(nextPosition) * 32767)));
                ready.WriteByte((byte)(value & 0xFF));
                ready.WriteByte((byte)((value >> 8) & 0xFF));
                nextPosition += step;
            }
            // Keep only the samples the next output still needs.
            long keepFrom = (long)Math.Floor(nextPosition) - HalfTaps;
            int drop = (int)Math.Min(window.Count, keepFrom - windowStart);
            if (drop > 0) { window.RemoveRange(0, drop); windowStart += drop; }
        }

        private double Sample(double position)
        {
            long center = (long)Math.Floor(position);
            double sum = 0, weight = 0;
            for (long n = center - HalfTaps + 1; n <= center + HalfTaps; n++)
            {
                double w = Kernel(position - n);
                long index = n - windowStart;
                float x = index >= 0 && index < window.Count ? window[(int)index] : 0f;
                sum += x * w;
                weight += w;
            }
            return weight == 0 ? 0 : sum / weight;
        }

        // Windowed-sinc low-pass; normalized by the caller so DC gain is exactly one.
        private double Kernel(double tau)
        {
            double r = Math.Abs(tau) / (HalfTaps + 1);
            if (r >= 1) return 0;
            double x = 2 * normalizedCutoff * tau;
            double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double hann = 0.5 + 0.5 * Math.Cos(Math.PI * r);
            return 2 * normalizedCutoff * sinc * hann;
        }
    }
}
