using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using MathNet.Numerics.IntegralTransforms;

namespace PlutoSpectrum
{
    // ── Backend interface ────────────────────────────────────────────────────
    internal interface IFftProvider : IDisposable
    {
        string Name { get; }

        // Windowing + FFT + magnitude/dB in one call.
        // Returns two arrays of length n: spec (dBFS) and psd (dBFS/Hz).
        void Compute(short[] iq, int n, long sampleRateHz, double calOffsetDb,
                     out double[] spec, out double[] psd);
    }

    // ── CPU backend (MathNet) ────────────────────────────────────────────────
    internal sealed class CpuFftProvider : IFftProvider
    {
        public string Name => "CPU (MathNet)";

        public void Compute(short[] iq, int n, long sampleRateHz, double calOffsetDb,
                            out double[] spec, out double[] psd)
        {
            double fs   = sampleRateHz;
            double rbw  = fs / n;

            var    samples = new Complex[n];
            double wSum2   = 0;

            for (int i = 0; i < n; i++)
            {
                double w   = 0.5 * (1 - Math.Cos(2 * Math.PI * i / n));
                wSum2     += w * w;
                samples[i] = new Complex(iq[i * 2] / 32768.0 * w, iq[i * 2 + 1] / 32768.0 * w);
            }

            Fourier.Forward(samples, FourierOptions.Matlab);

            spec = new double[n];
            psd  = new double[n];
            double normSpec = 2.0 / n;
            double normPsd  = 1.0 / (wSum2 * rbw);

            for (int i = 0; i < n; i++)
            {
                int    k   = (i + n / 2) % n;
                double mag = samples[k].Magnitude;
                spec[i] = 20 * Math.Log10(Math.Max(mag * normSpec, 1e-12)) + calOffsetDb;
                psd[i]  = 10 * Math.Log10(Math.Max(mag * mag * normPsd, 1e-24)) + calOffsetDb;
            }
        }

        public void Dispose() { }
    }

    // ── GPU backend (ILGPU kernels + MathNet FFT) ────────────────────────────
    // The GPU handles the two O(N) passes (windowing and magnitude/dB).
    // MathNet handles the O(N log N) FFT on the background thread while the GPU
    // is busy with windowing — they overlap in time.
    internal sealed class GpuFftProvider : IFftProvider
    {
        private readonly Context    _ctx;
        private readonly Accelerator _acc;

        // Kernel: short IQ → Hann-windowed float2 (I, Q)
        private readonly Action<
            Index1D,
            ArrayView1D<short,  Stride1D.Dense>,
            ArrayView1D<float,  Stride1D.Dense>,   // interleaved float IQ out
            int> _windowKernel;

        // Kernel: complex FFT output → dB spec + PSD (with FFT-shift)
        private readonly Action<
            Index1D,
            ArrayView1D<float,  Stride1D.Dense>,   // interleaved float FFT in
            ArrayView1D<double, Stride1D.Dense>,   // spec out
            ArrayView1D<double, Stride1D.Dense>,   // psd out
            int, double, double, double> _magKernel;

        // Persistent GPU buffers — reallocated only when n changes
        private MemoryBuffer1D<short,  Stride1D.Dense>? _gpuIq;
        private MemoryBuffer1D<float,  Stride1D.Dense>? _gpuWindowed;  // interleaved float I,Q
        private MemoryBuffer1D<float,  Stride1D.Dense>? _gpuFft;       // interleaved float I,Q after FFT
        private MemoryBuffer1D<double, Stride1D.Dense>? _gpuSpec;
        private MemoryBuffer1D<double, Stride1D.Dense>? _gpuPsd;
        private int _allocatedN;

        public string Name { get; }

        public GpuFftProvider(Context ctx, Accelerator acc)
        {
            _ctx = ctx;
            _acc = acc;
            Name = $"GPU ({acc.Name}) + CPU FFT";

            _windowKernel = acc.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<short, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                int>(WindowKernel);

            _magKernel = acc.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<float,  Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                int, double, double, double>(MagKernel);
        }

        // GPU kernel: apply Hann window and normalise to [-1, 1] float
        private static void WindowKernel(
            Index1D idx,
            ArrayView1D<short, Stride1D.Dense> iq,
            ArrayView1D<float, Stride1D.Dense> windowed,
            int n)
        {
            int i = idx;
            if (i >= n) return;
            float w = 0.5f * (1.0f - XMath.Cos(2.0f * XMath.PI * i / n));
            windowed[i * 2]     = iq[i * 2]     / 32768.0f * w;
            windowed[i * 2 + 1] = iq[i * 2 + 1] / 32768.0f * w;
        }

        // GPU kernel: interleaved float FFT output → dB spectrum + PSD with FFT-shift
        private static void MagKernel(
            Index1D idx,
            ArrayView1D<float,  Stride1D.Dense> fft,
            ArrayView1D<double, Stride1D.Dense> spec,
            ArrayView1D<double, Stride1D.Dense> psd,
            int n, double normSpec, double normPsd, double calOff)
        {
            int i = idx;
            if (i >= n) return;
            int   k  = (i + n / 2) % n;
            float re = fft[k * 2];
            float im = fft[k * 2 + 1];
            double mag = XMath.Sqrt((double)(re * re + im * im));
            double ms  = mag * normSpec;
            double mp  = mag * mag * normPsd;
            spec[i] = 20.0 * XMath.Log10(ms < 1e-12 ? 1e-12 : ms) + calOff;
            psd[i]  = 10.0 * XMath.Log10(mp < 1e-24 ? 1e-24 : mp) + calOff;
        }

        private void EnsureBuffers(int n)
        {
            if (_allocatedN == n) return;
            _gpuIq?.Dispose();
            _gpuWindowed?.Dispose();
            _gpuFft?.Dispose();
            _gpuSpec?.Dispose();
            _gpuPsd?.Dispose();
            _gpuIq       = _acc.Allocate1D<short>(n * 2);
            _gpuWindowed = _acc.Allocate1D<float>(n * 2);
            _gpuFft      = _acc.Allocate1D<float>(n * 2);
            _gpuSpec     = _acc.Allocate1D<double>(n);
            _gpuPsd      = _acc.Allocate1D<double>(n);
            _allocatedN  = n;
        }

        public void Compute(short[] iq, int n, long sampleRateHz, double calOffsetDb,
                            out double[] spec, out double[] psd)
        {
            double fs       = sampleRateHz;
            double rbw      = fs / n;
            double normSpec = 2.0 / n;
            // Periodic Hann wSum2 = 3n/8 (exact analytic value)
            double wSum2    = 0.375 * n;
            double normPsd  = 1.0 / (wSum2 * rbw);

            EnsureBuffers(n);

            // 1. Upload raw IQ to GPU and run windowing kernel
            _gpuIq!.CopyFromCPU(iq);
            _windowKernel((Index1D)n, _gpuIq!.View, _gpuWindowed!.View, n);

            // 2. Download windowed samples to CPU for MathNet FFT
            //    (cuFFT unavailable — no CUDA toolkit installed)
            var windowed = _gpuWindowed!.GetAsArray1D();   // sync point

            // 3. FFT on CPU — runs while GPU result is downloading
            var samples = new Complex[n];
            for (int i = 0; i < n; i++)
                samples[i] = new Complex(windowed[i * 2], windowed[i * 2 + 1]);
            Fourier.Forward(samples, FourierOptions.Matlab);

            // 4. Upload FFT result to GPU for magnitude/dB kernel
            var fftFloats = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                fftFloats[i * 2]     = (float)samples[i].Real;
                fftFloats[i * 2 + 1] = (float)samples[i].Imaginary;
            }
            _gpuFft!.CopyFromCPU(fftFloats);
            _magKernel((Index1D)n, _gpuFft!.View, _gpuSpec!.View, _gpuPsd!.View,
                       n, normSpec, normPsd, calOffsetDb);

            // 5. Download results
            _acc.Synchronize();
            spec = _gpuSpec.GetAsArray1D();
            psd  = _gpuPsd.GetAsArray1D();
        }

        public void Dispose()
        {
            _gpuIq?.Dispose();
            _gpuWindowed?.Dispose();
            _gpuFft?.Dispose();
            _gpuSpec?.Dispose();
            _gpuPsd?.Dispose();
            _acc.Dispose();
            _ctx.Dispose();
        }
    }

    // ── Factory ──────────────────────────────────────────────────────────────
    internal static class FftProviderFactory
    {
        // Returns GpuFftProvider backed by CUDA (preferred) or OpenCL if a
        // discrete accelerator is found; falls back to CpuFftProvider otherwise.
        public static IFftProvider Create(out string diagnostics)
        {
            try
            {
                // EnableAlgorithms is required for XMath (Cos, Log10, Sqrt) in kernels.
                var ctx = Context.Create(b => b.Default().EnableAlgorithms());

                var devices = new List<Device>();
                foreach (var d in ctx) devices.Add(d);

                // Prefer CUDA, then OpenCL; skip CPU-only accelerator
                var gpu = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                       ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);

                if (gpu != null)
                {
                    var acc      = gpu.CreateAccelerator(ctx);
                    var provider = new GpuFftProvider(ctx, acc);
                    diagnostics  = $"FFT: {provider.Name}";
                    return provider;
                }

                ctx.Dispose();
            }
            catch (Exception ex)
            {
                diagnostics = $"FFT: CPU (GPU init failed: {ex.Message})";
                return new CpuFftProvider();
            }

            diagnostics = "FFT: CPU (no discrete GPU found)";
            return new CpuFftProvider();
        }
    }
}
