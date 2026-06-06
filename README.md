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
- **Three synchronized displays** — Spectrum, PSD, and Waterfall
- **Spectrum-analyzer style controls**

| Control | Description |
|---|---|
| **Center / Start / Stop** | Frequency entry in MHz, 70 – 7000 |
| **Span** | 200 kHz → 20 MHz → Full — drives the ADC sample rate automatically |
| **RBW** | Resolution Bandwidth (1 kHz – 1 MHz) — auto-computes FFT size |
| **VBW** | Video Bandwidth — post-detection averaging depth (1 kHz – 3 MHz) |
| **Ref Level** | Shifts the top of the Y-axis, just like a bench spectrum analyzer |
| **dB/div** | Vertical scale: 1, 2, 5, 10, or 20 dB per division |
| **CAL offset** | Calibration offset (dBFS → dBm) — tune until a known signal reads correctly |
| **Gain** | Hardware gain 0 – 73 dB (AD9364) |
| **RF BW** | Analog front-end filter bandwidth |
| **ANT** | Select RX antenna port (A_BALANCED / B_BALANCED) |

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
    ├── MainWindow.xaml      # WPF UI — all controls and plots
    ├── MainWindow.xaml.cs   # Acquisition loop, DSP (FFT/windowing/averaging)
    ├── App.xaml / App.xaml.cs
    └── PlutoSpectrum.csproj
```

---

## Architecture

```
UI (WPF / XAML)
    │  events / data-binding
    ▼
MainWindow.xaml.cs
    │  async Task.Run
    ├──▶ DSP pipeline: IQ samples → Hann window → FFT → dBFS → avg → cal offset
    │         ├── Spectrum plot  (ScottPlot SignalXY)
    │         ├── PSD plot       (ScottPlot SignalXY)
    │         └── Waterfall      (ScottPlot Heatmap, scrolling 200-row matrix)
    │
    └──▶ PlutoSdr.cs  (libiio P/Invoke)
             ├── iio_create_network_context
             ├── ad9361-phy  → set LO freq, sample rate, RF BW, gain
             └── cf-ad9361-lpc → stream 16-bit IQ samples
```

---

## License

MIT — see [LICENSE](LICENSE).
