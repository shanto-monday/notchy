// Live audio spectrum for the Home player: captures what the speakers play (WASAPI loopback),
// runs an FFT and folds it into a few log-spaced bands. Runs only while the notch shows it.
using NAudio.Wave;

namespace Notchy;

sealed class Spectrum : IDisposable
{
    public const int Bands = 32;
    const int N = 2048;
    WasapiLoopbackCapture cap;
    readonly float[] ring = new float[N];
    int ringPos, channels = 2, rate = 48000;
    readonly object gate = new();
    readonly float[] bands = new float[Bands];
    readonly float[] peakNorm = { 0.02f };
    bool running;

    public void Start()
    {
        if (running) return;
        running = true;
        Audio.Post(() =>
        {
            if (!running) return;   // stopped before this ran
            try
            {
                cap = new WasapiLoopbackCapture();
                channels = cap.WaveFormat.Channels; rate = cap.WaveFormat.SampleRate;
                bool isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || cap.WaveFormat.BitsPerSample == 32;
                cap.DataAvailable += (_, e) =>
                {
                    int step = channels * (isFloat ? 4 : 2);
                    lock (gate)
                        for (int i = 0; i + step <= e.BytesRecorded; i += step)
                        {
                            float v = 0;
                            for (int c = 0; c < channels; c++)
                                v += isFloat ? BitConverter.ToSingle(e.Buffer, i + c * 4) : BitConverter.ToInt16(e.Buffer, i + c * 2) / 32768f;
                            ring[ringPos] = v / channels; ringPos = (ringPos + 1) % N;
                        }
                };
                cap.StartRecording();
            }
            catch (Exception ex) { Dbg.Log("spectrum " + ex.Message); running = false; }
        });
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        var c = cap; cap = null;
        Audio.Post(() => { try { c?.StopRecording(); c?.Dispose(); } catch { } });
        lock (gate) { Array.Clear(ring); }
    }

    // 0..1 per band
    public float[] Read()
    {
        var re = new double[N]; var im = new double[N];
        lock (gate)
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)); // Hann window
                re[i] = ring[(ringPos + i) % N] * w;
            }
        Fft(re, im);
        double fLo = 40, fHi = Math.Min(16000, rate / 2.0);
        float max = 0;
        for (int b = 0; b < Bands; b++)
        {
            double a = fLo * Math.Pow(fHi / fLo, b / (double)Bands), z = fLo * Math.Pow(fHi / fLo, (b + 1) / (double)Bands);
            int i0 = Math.Max(1, (int)(a * N / rate)), i1 = Math.Max(i0 + 1, (int)(z * N / rate));
            double sum = 0;
            for (int i = i0; i < i1 && i < N / 2; i++) sum = Math.Max(sum, Math.Sqrt(re[i] * re[i] + im[i] * im[i]));
            // treble is naturally quieter: tilt it up a little
            float v = (float)(sum * (1 + b * 0.09));
            bands[b] = v; max = Math.Max(max, v);
        }
        // auto gain with slow fall so quiet songs still move
        peakNorm[0] = Math.Max(max, peakNorm[0] * 0.985f);
        float g = Math.Max(peakNorm[0], 0.5f);
        var res = new float[Bands];
        for (int b = 0; b < Bands; b++) res[b] = (float)Math.Round(Math.Clamp(Math.Pow(bands[b] / g, 0.7), 0, 1), 3);
        return res;
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
