# PlutoSpectrum

A real-time spectrum analyzer for the **ADALM-Pluto / Pluto+** SDR, built with C# / WPF and .NET 9.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-9.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Features

- **70 MHz – 7 GHz** coverage (Pluto+ extended firmware required above 6 GHz)
- **Live mode** — continuous FFT at a fixed center frequency
- **Span Sweep** — sweep a user-defined start/stop range
- **Full Sweep** — automated sweep across the entire 70 MHz – 7 GHz band
- **Spectrum plot** — always visible, main display
- **Switchable secondary panel** — Waterfall, PSD, IQ Constellation, or hidden
- **GPU-accelerated DSP** — ILGPU backend auto-selected at startup (CUDA → OpenCL → CPU fallback)

### Controls

| Control | Description |
|---|---|
| **Center / Start / Stop** | Frequency entry in MHz, 70 – 7000 |
| **Max Freq** | Upper frequency limit in MHz — set to 6000 for standard firmware, up to ~6999 for extended Pluto+ firmware |
| **Span** | 200 kHz → 20 MHz → Full — drives the ADC sample rate automatically |
| **RBW** | Resolution Bandwidth (1 kHz – 1 MHz) — auto-computes FFT size |
| **VBW** | Video Bandwidth — post-detection averaging depth (1 kHz – 3 MHz) |
| **Ref Level** | Shifts the top of the Y-axis, just like a bench spectrum analyzer |
| **dB/div** | Vertical scale: 1, 2, 5, 10, or 20 dB per division |
| **CAL offset** | Calibration offset (dBFS → dBm) — tune until a known signal reads correctly |
| **Gain** | Hardware gain 0 – 73 dB (AD9364) |
| **RF BW** | Analog front-end filter bandwidth |
| **ANT** | Select RX antenna port (A_BALANCED / B_BALANCED) |
| **Bottom view** | Switch secondary panel: Waterfall / PSD / IQ / None |

---

## Requirements

| Dependency | Notes |
|---|---|
| Windows 10/11 x64 | WPF requires Windows |
| [.NET 9 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) | Desktop runtime |
| [libiio](https://github.com/analogdevicesinc/libiio/releases) | Analog Devices IIO library — place `libiio.dll` next to the exe or install system-wide |
| ADALM-Pluto or Pluto+ | Connected via USB (RNDIS) or network; default IP `192.168.2.1` |

### NuGet packages (auto-restored on build)

- [MathNet.Numerics](https://www.nuget.org/packages/MathNet.Numerics/) — FFT
- [ScottPlot.WPF](https://www.nuget.org/packages/ScottPlot.WPF/) — interactive plots
- [ILGPU](https://www.nuget.org/packages/ILGPU/) — GPU-accelerated windowing and magnitude/dB kernels
- [ILGPU.Algorithms](https://www.nuget.org/packages/ILGPU.Algorithms/) — `XMath` functions used in GPU kernels

---

## Getting started

```bash
git clone https://github.com/EMonatko/PlutoSpectrum.git
cd PlutoSpectrum
dotnet build PlutoSpectrum/PlutoSpectrum.csproj
dotnet run --project PlutoSpectrum/PlutoSpectrum.csproj
```

Or open `PlutoSpectrum.sln` in Visual Studio 2022 and press **F5**.

Make sure `libiio.dll` is on your PATH or in the output folder (`bin\Debug\net9.0-windows\`).

---

## GPU acceleration

At startup `FftProviderFactory.Create()` probes for a discrete GPU via ILGPU:

1. **CUDA** accelerator (NVIDIA) — preferred if found
2. **OpenCL** accelerator (AMD / Intel) — fallback if no CUDA
3. **CPU (MathNet)** — used when no discrete GPU is detected or GPU init fails

The selected backend is shown in the status bar on startup (e.g. `FFT: GPU (NVIDIA GeForce …) + CPU FFT`).

The GPU handles the two O(N) passes — Hann windowing and magnitude/dB computation — while MathNet runs the O(N log N) FFT on the background thread. This pipeline overlaps windowing and FFT work in time.

---

## Extended frequency range (Pluto+ only)

The standard AD9364 firmware accepts LO frequencies up to 6 GHz. Patched Pluto+ firmware can reach ~6.999 GHz. To unlock the extended range:

1. Flash the extended Pluto+ firmware on your device
2. In the **Max Freq** field enter a value up to `6999` (MHz) and press Enter
3. PlutoSpectrum passes the higher limit to `PlutoSdr.Open()` and clamps all LO writes within `[70 MHz, max]`

If your firmware is not patched, writing frequencies above 6 GHz returns `EINVAL (-22)` from libiio; PlutoSpectrum catches this and prints a warning rather than crashing.

---

## Calibration tip (dBFS → dBm)

The Y-axis shows **dBFS** (relative to ADC full scale) by default. To read approximate dBm:

1. Set hardware **Gain** (e.g. 40 dB)
2. Point at a signal whose power you know (e.g. Wi-Fi at 2.4 GHz — check RSSI on your phone)
3. Enter a **CAL** offset until the peak on screen matches the known value
   - Starting point: `CAL ≈ −gain_dB − 10`  (e.g. −50 dB at 40 dB gain)
4. The Y-axis label changes to **"Power (dBm est.)"**

> **Note:** The AD9364 front-end loss increases with frequency, so the cal offset will differ at 2.4 GHz vs 433 MHz. Calibrate at the band you care about.

---

## Project structure

```
PlutoSpectrum/
├── PlutoSpectrum.sln
└── PlutoSpectrum/
    ├── PlutoSdr.cs          # libiio P/Invoke wrapper — hardware abstraction
    ├── FftProvider.cs       # IFftProvider interface + CPU and GPU backends + factory
    ├── MainWindow.xaml      # WPF UI — Catppuccin Mocha theme, all controls and plots
    ├── MainWindow.xaml.cs   # Acquisition loop, DSP pipeline, sweep engine, UI logic
    ├── App.xaml / App.xaml.cs
    └── PlutoSpectrum.csproj
```

---

## Architecture

```
UI (WPF / XAML — Catppuccin Mocha theme)
    │  events / data-binding
    ▼
MainWindow.xaml.cs
    │  async Task.Run
    ├──▶ DSP pipeline
    │         IQ samples
    │           → IFftProvider.Compute()          ← GPU (ILGPU) or CPU (MathNet)
    │               ├── Hann window               ← GPU kernel (O(N))
    │               ├── FFT                       ← MathNet (O(N log N))
    │               └── magnitude/dB + cal offset ← GPU kernel (O(N))
    │           → exponential averaging (VBW)
    │           → Spectrum plot  (ScottPlot SignalXY)
    │           → Secondary panel (switchable):
    │               ├── Waterfall   (ScottPlot Heatmap, 200-row scrolling matrix)
    │               ├── PSD         (ScottPlot SignalXY)
    │               └── IQ Constellation (ScottPlot Scatter, unit-circle reference)
    │
    └──▶ PlutoSdr.cs  (libiio P/Invoke)
             ├── iio_create_network_context
             ├── ad9361-phy  → set LO freq, sample rate, RF BW, gain
             └── cf-ad9361-lpc → stream 16-bit IQ samples
```

---

## License

MIT — see [LICENSE](LICENSE).
