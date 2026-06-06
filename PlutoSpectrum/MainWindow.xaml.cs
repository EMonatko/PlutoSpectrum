using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MathNet.Numerics.IntegralTransforms;
using ScottPlot;
using ScottPlot.Colormaps;

namespace PlutoSpectrum
{
    public partial class MainWindow : Window
    {
        private const double FreqMinMhz   = PlutoSdr.LoMinHz / 1e6;
        private const double FreqMaxMhz   = PlutoSdr.LoMaxHz / 1e6;
        private const int    WaterfallRows = 200;

        private PlutoSdr?                _sdr;
        private CancellationTokenSource? _cts;
        private bool                     _running;
        private bool                     _plotsReady;

        // ── Live mode ────────────────────────────────────────────────────────
        private double[] _freqAxis = [];
        private double[] _powerDb  = [];
        private double[] _psdDb    = [];

        private ScottPlot.Plottables.SignalXY? _specPlot;
        private ScottPlot.Plottables.SignalXY? _psdPlot;

        private double[]? _avgSpecBuf;
        private double[]? _avgPsdBuf;
        private int       _avgCount;

        // ── Sweep mode ───────────────────────────────────────────────────────
        private double[] _sweepFreq    = [];
        private double[] _sweepPowerDb = [];
        private double[] _sweepPsdDb   = [];

        private ScottPlot.Plottables.SignalXY? _sweepSpecPlot;
        private ScottPlot.Plottables.SignalXY? _sweepPsdPlot;

        // ── Waterfall ────────────────────────────────────────────────────────
        private double[,]                     _wfMatrix  = new double[0, 0];
        private ScottPlot.Plottables.Heatmap? _wfHeatmap;
        private int                           _wfBins;

        // ── Resolved SA parameters ───────────────────────────────────────────
        private long _centerFreqHz;
        private long _sampleRateHz;
        private long _rfBwHz;
        private int  _fftSize;
        private long _rbwHz;        // actual RBW in use
        private long _vbwHz;        // VBW setting (maps to averaging depth)
        private long _spanHz;       // display span

        // ── Display calibration ──────────────────────────────────────────────
        // calOffsetDb: added to every dBFS reading → shifts toward dBm.
        // Typical: at 40 dB gain, Pluto+ full-scale ≈ −10 dBm input,
        //   so offset ≈ −10 − 40 = −50 dB is a rough starting point.
        // User adjusts until a known signal (e.g. WiFi at −50 dBm RSSI) reads correctly.
        private double _calOffsetDb = 0.0;
        private double _refLevelDb  = 0.0;   // top of Y axis (dBFS + offset)
        private int    _dbPerDiv    = 100;   // full dynamic range shown (10 divisions)

        // ────────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            ConfigurePlots();
            _plotsReady = true;
            SldGain.ValueChanged += (_, e) =>
                { if (TxtGainVal is not null) TxtGainVal.Text = ((int)e.NewValue).ToString(); };
            UpdateDerivedReadouts();
            ApplyYAxisLimits();
        }

        // ── Plot setup ───────────────────────────────────────────────────────
        private void ConfigurePlots()
        {
            StyleLinePlot(SpectrumPlot.Plot, "Frequency (MHz)", "Power (dBFS)",  -100, 0);
            StyleLinePlot(PsdPlot.Plot,      "Frequency (MHz)", "PSD (dBFS/Hz)", -180, -60);
            StyleWaterfallPlot(WaterfallPlot.Plot);

            SpectrumPlot.UserInputProcessor.IsEnabled  = true;
            PsdPlot.UserInputProcessor.IsEnabled       = true;
            WaterfallPlot.UserInputProcessor.IsEnabled = true;

            SpectrumPlot.MouseMove  += PlotMouseMove;
            PsdPlot.MouseMove       += PlotMouseMove;
            WaterfallPlot.MouseMove += PlotMouseMove;
        }

        private static void StyleLinePlot(ScottPlot.Plot plt, string xLabel, string yLabel,
                                          double yMin, double yMax)
        {
            plt.FigureBackground.Color      = Color.FromHex("#1E1E2E");
            plt.DataBackground.Color        = Color.FromHex("#181825");
            plt.Axes.Color(Color.FromHex("#CDD6F4"));
            plt.Grid.MajorLineColor         = Color.FromHex("#313244");
            plt.Axes.Bottom.Label.Text      = xLabel;
            plt.Axes.Left.Label.Text        = yLabel;
            plt.Axes.Bottom.Label.ForeColor = Color.FromHex("#89B4FA");
            plt.Axes.Left.Label.ForeColor   = Color.FromHex("#A6E3A1");
            plt.Axes.SetLimitsY(yMin, yMax);
        }

        private static void StyleWaterfallPlot(ScottPlot.Plot plt)
        {
            plt.FigureBackground.Color      = Color.FromHex("#1E1E2E");
            plt.DataBackground.Color        = Color.FromHex("#000000");
            plt.Axes.Color(Color.FromHex("#CDD6F4"));
            plt.Grid.IsVisible              = false;
            plt.Axes.Bottom.Label.Text      = "Frequency (MHz)";
            plt.Axes.Left.Label.Text        = "Time →";
            plt.Axes.Bottom.Label.ForeColor = Color.FromHex("#89B4FA");
            plt.Axes.Left.Label.ForeColor   = Color.FromHex("#F38BA8");
        }

        // ── UI events ────────────────────────────────────────────────────────
        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_running) StopAcquisition();
            else          StartAcquisition();
        }

        private void BtnApplyFreq_Click(object sender, RoutedEventArgs e)
        {
            if (!_running) return;
            string mode = GetMode();

            if (mode == "live")
            {
                if (!TryParseFreqMhz(TxtCenterFreq.Text, out double mhz)) return;
                _centerFreqHz = (long)(mhz * 1e6);
                _sdr?.SetCenterFreq(_centerFreqHz);
                ResetWaterfall(_fftSize, _freqAxis[0], _freqAxis[^1]);
                UpdateLiveFreqAxis();
                UpdateStatusBar();
            }
            else if (mode == "sweep")
            {
                // Restart with new start/stop
                StopAcquisition();
                StartAcquisition();
            }
        }

        private void SldGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtGainVal is null) return;
            TxtGainVal.Text = ((int)e.NewValue).ToString();
            _sdr?.SetGain(e.NewValue);
        }

        private void CmbSampleRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDerivedReadouts();
            if (_running) { StopAcquisition(); StartAcquisition(); }
        }

        private void CmbSpan_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDerivedReadouts();
            if (_running) { StopAcquisition(); StartAcquisition(); }
        }

        private void CmbRbw_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDerivedReadouts();
        }

        private void CmbVbw_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDerivedReadouts();
        }

        private void CmbDbDiv_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _dbPerDiv = (int)SelectedLongTag(CmbDbDiv, 100);
            ApplyYAxisLimits();
        }

        private void TxtRefLevel_LostFocus(object sender, RoutedEventArgs e)  => ParseRefLevel();
        private void TxtCalOffset_LostFocus(object sender, RoutedEventArgs e) => ParseCalOffset();

        private void TxtRefLevel_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ParseRefLevel();
        }

        private void TxtCalOffset_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ParseCalOffset();
        }

        private void ParseRefLevel()
        {
            if (double.TryParse(TxtRefLevel.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                _refLevelDb = v;
                ApplyYAxisLimits();
            }
        }

        private void ParseCalOffset()
        {
            if (double.TryParse(TxtCalOffset.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                _calOffsetDb = v;
                ApplyYAxisLimits();
            }
        }

        // Recomputes Y-axis limits and label from ref level, dB/div and cal offset.
        // Called whenever any of those three change; safe to call while running.
        private void ApplyYAxisLimits()
        {
            if (!_plotsReady) return;
            double top    = _refLevelDb + _calOffsetDb;
            double bottom = top - _dbPerDiv;
            string yLabel = _calOffsetDb != 0.0 ? "Power (dBm est.)" : "Power (dBFS)";
            string pLabel = _calOffsetDb != 0.0 ? "PSD (dBm/Hz est.)" : "PSD (dBFS/Hz)";

            Dispatcher.Invoke(() =>
            {
                SpectrumPlot.Plot.Axes.SetLimitsY(bottom, top);
                PsdPlot.Plot.Axes.SetLimitsY(bottom - 80, top - 20);
                SpectrumPlot.Plot.Axes.Left.Label.Text = yLabel;
                PsdPlot.Plot.Axes.Left.Label.Text      = pLabel;
                SpectrumPlot.Refresh();
                PsdPlot.Refresh();
                // Waterfall colour range tracks the same window
                if (_wfHeatmap != null)
                {
                    _wfHeatmap.ManualRange = new ScottPlot.Range(bottom, top);
                    WaterfallPlot.Refresh();
                }
            });
        }

        private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string mode = GetMode();
            // Enable/disable start/stop freq fields
            bool isSweep = mode == "sweep" || mode == "fullsweep";
            if (TxtStartFreq != null) TxtStartFreq.IsEnabled = isSweep;
            if (TxtStopFreq  != null) TxtStopFreq.IsEnabled  = isSweep;
            if (TxtCenterFreq != null) TxtCenterFreq.IsEnabled = (mode == "live");
            UpdateDerivedReadouts();
        }

        private void PlotMouseMove(object sender, MouseEventArgs e)
        {
            var freqs = (GetMode() == "live") ? _freqAxis : _sweepFreq;
            if (freqs.Length == 0) return;
            if (sender is not ScottPlot.WPF.WpfPlot plot) return;
            var pos   = e.GetPosition(plot);
            var coord = plot.Plot.GetCoordinates((float)pos.X, (float)pos.Y);
            double freqMhz = coord.X;
            TxtMouseFreq.Text = $"{freqMhz:F4} MHz";
        }

        private void Window_Closed(object sender, EventArgs e) => StopAcquisition();

        // ── Parameter resolution ─────────────────────────────────────────────
        // Derives the actual hardware parameters from the UI selectors.
        // Priority: Span → sample rate; RBW → FFT size.

        private (long sampleRateHz, int fftSize, long rbwHz, long vbwHz, long spanHz)
            ResolveParameters()
        {
            long spanTag = SelectedLongTag(CmbSpan, 5_000_000);
            long fsTag   = SelectedLongTag(CmbSampleRate, 5_000_000);
            long rbwTag  = SelectedLongTag(CmbRbw, 0);
            long vbwTag  = SelectedLongTag(CmbVbw, 100_000);
            int  fftTag  = (int)SelectedLongTag(CmbFftSize, 4096);

            // Span drives sample rate (Full Sweep uses whatever Fs is selected)
            long fs = spanTag > 0 ? SpanToSampleRate(spanTag) : fsTag;

            // RBW drives FFT size: FFT = fs / rbw  (rounded to power-of-2, clamped)
            int fft;
            long actualRbw;
            if (rbwTag > 0)
            {
                int ideal = (int)Math.Round((double)fs / rbwTag);
                fft = ClampFftSize(NearestPow2(ideal));
                actualRbw = fs / fft;
            }
            else
            {
                // Auto or manual FFT tag
                fft = fftTag;
                actualRbw = fs / fft;
            }

            // VBW maps to an averaging depth: avgDepth = rbw / vbw (clamped 1..200)
            // Real SAs integrate this way: the VBW filter is post-detection.
            long actualVbw = vbwTag;

            long span = spanTag > 0 ? spanTag : fs;

            return (fs, fft, actualRbw, actualVbw, span);
        }

        // Snap span to the nearest available hardware sample rate (must be ≤ span)
        private static long SpanToSampleRate(long spanHz) => spanHz switch
        {
            <= 200_000   => 200_000,
            <= 500_000   => 500_000,
            <= 1_000_000 => 1_000_000,
            <= 2_000_000 => 2_000_000,
            <= 5_000_000 => 5_000_000,
            <= 10_000_000 => 10_000_000,
            _             => 20_000_000
        };

        private static int NearestPow2(int n)
        {
            if (n <= 1) return 1;
            int p = 1;
            while (p < n) p <<= 1;
            // Choose whichever power of 2 is closer
            return (p - n) < (n - p / 2) ? p : p / 2;
        }

        private static int ClampFftSize(int n)
            => Math.Clamp(n, 1024, 32768);

        // How many averages correspond to VBW?  avgDepth = rbw / vbw
        private static int VbwToAvgDepth(long rbwHz, long vbwHz)
        {
            if (vbwHz <= 0 || rbwHz <= 0) return 1;
            int depth = (int)Math.Round((double)rbwHz / vbwHz);
            return Math.Clamp(depth, 1, 200);
        }

        private void UpdateDerivedReadouts()
        {
            var (fs, fft, rbw, vbw, span) = ResolveParameters();

            if (TxtRbwActual != null) TxtRbwActual.Text = FormatHz(rbw);
            if (TxtVbwActual != null) TxtVbwActual.Text = FormatHz(vbw);
            if (TxtSpanActual != null)
            {
                long spanTag = SelectedLongTag(CmbSpan, 0);
                TxtSpanActual.Text = spanTag == 0 ? "Full" : FormatHz(span);
            }
        }

        private static string FormatHz(long hz)
        {
            if (hz >= 1_000_000) return $"{hz / 1_000_000.0:G3} MHz";
            if (hz >= 1_000)     return $"{hz / 1000.0:G3} kHz";
            return $"{hz} Hz";
        }

        // ── Acquisition dispatch ─────────────────────────────────────────────
        private void StartAcquisition()
        {
            var (fs, fft, rbw, vbw, span) = ResolveParameters();
            _sampleRateHz = fs;
            _fftSize      = fft;
            _rbwHz        = rbw;
            _vbwHz        = vbw;
            _spanHz       = span;
            _rfBwHz       = SelectedLongTag(CmbRfBw, 5_000_000);

            string ip = TxtPlutoIp.Text.Trim();
            if (string.IsNullOrEmpty(ip)) ip = PlutoSdr.DefaultIp;

            string mode = GetMode();

            long openFreq;
            if (mode == "live")
            {
                if (!TryParseFreqMhz(TxtCenterFreq.Text, out double mhz))
                {
                    TxtStatus.Text = $"Invalid center frequency — enter {FreqMinMhz}–{FreqMaxMhz} MHz.";
                    return;
                }
                openFreq = (long)(mhz * 1e6);
            }
            else
            {
                // Sweep modes: open at start freq
                if (!TryParseFreqMhz(TxtStartFreq.Text, out double startMhz))
                    startMhz = FreqMinMhz;
                openFreq = (long)(startMhz * 1e6);
            }

            TxtStatus.Text = $"Connecting… fc={openFreq/1e6:F3} MHz  fs={_sampleRateHz/1e6:F3} MSPS  RBW={FormatHz(_rbwHz)}";

            string rxAnt = (CmbRxAntenna.SelectedItem as ComboBoxItem)?.Tag as string ?? "A_BALANCED";

            _sdr = new PlutoSdr();
            try { _sdr.Open(openFreq, _sampleRateHz, _rfBwHz, SldGain.Value, _fftSize, ip, rxAnt); }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error: {ex.Message}";
                if (_sdr.RxPortsAvailable.Length > 0)
                    PopulateRxAntennaPorts(_sdr.RxPortsAvailable);
                _sdr.Dispose(); _sdr = null;
                return;
            }

            _running = true;
            BtnStartStop.Content   = "■  STOP";
            BtnStartStop.Style     = (Style)FindResource("DarkButton");
            BtnApplyFreq.IsEnabled = true;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            UpdateDerivedReadouts();

            if      (mode == "live")      StartLive(openFreq, token);
            else if (mode == "sweep")     StartSweep(token, fullRange: false);
            else                          StartSweep(token, fullRange: true);
        }

        private void StopAcquisition()
        {
            _running = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _sdr?.Dispose();
            _sdr = null;

            Dispatcher.Invoke(() =>
            {
                BtnStartStop.Content   = "▶  START";
                BtnStartStop.Style     = (Style)FindResource("StartButton");
                BtnApplyFreq.IsEnabled = true;
                TxtStatus.Text         = "Stopped.";
                TxtSaInfo.Text         = "";
                SpectrumPlot.Plot.Clear();
                PsdPlot.Plot.Clear();
                WaterfallPlot.Plot.Clear();
                _specPlot = _psdPlot = _sweepSpecPlot = _sweepPsdPlot = null;
                _wfHeatmap = null;
                SpectrumPlot.Refresh();
                PsdPlot.Refresh();
                WaterfallPlot.Refresh();
            });
        }

        // ── Live mode ────────────────────────────────────────────────────────
        private void StartLive(long centerHz, CancellationToken token)
        {
            _centerFreqHz = centerHz;
            _avgSpecBuf   = null;
            _avgPsdBuf    = null;
            _avgCount     = 0;
            _powerDb      = new double[_fftSize];
            _psdDb        = new double[_fftSize];

            UpdateLiveFreqAxis();

            double fMin = _freqAxis[0];
            double fMax = _freqAxis[^1];

            ResetWaterfall(_fftSize, fMin, fMax);

            _specPlot           = SpectrumPlot.Plot.Add.SignalXY(_freqAxis, _powerDb);
            _specPlot.Color     = Color.FromHex("#89B4FA");
            _specPlot.LineWidth = 1;

            _psdPlot            = PsdPlot.Plot.Add.SignalXY(_freqAxis, _psdDb);
            _psdPlot.Color      = Color.FromHex("#A6E3A1");
            _psdPlot.LineWidth  = 1;

            UpdateStatusBar();

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested && _sdr != null)
                {
                    try
                    {
                        short[] raw = _sdr.ReadSamples();
                        int avgDepth = VbwToAvgDepth(_rbwHz, _vbwHz);
                        ComputeSpectrumAndPsd(raw, out double[] spec, out double[] psd);
                        AccumulateAverage(spec, psd, avgDepth);
                        PushWaterfallRow(_powerDb);
                        Dispatcher.Invoke(() =>
                        {
                            _wfHeatmap?.Update();
                            SpectrumPlot.Refresh();
                            PsdPlot.Refresh();
                            WaterfallPlot.Refresh();
                        });
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                }
            }, token);
        }

        private void UpdateLiveFreqAxis()
        {
            int    n    = _fftSize;
            double fc   = _centerFreqHz / 1e6;
            double bw   = _sampleRateHz / 1e6;
            double step = bw / n;

            _freqAxis = new double[n];
            for (int i = 0; i < n; i++)
                _freqAxis[i] = fc - bw / 2 + i * step;

            Dispatcher.Invoke(() =>
            {
                SpectrumPlot.Plot.Axes.SetLimitsX(_freqAxis[0], _freqAxis[n - 1]);
                PsdPlot.Plot.Axes.SetLimitsX(_freqAxis[0], _freqAxis[n - 1]);
            });
        }

        // ── Sweep mode ───────────────────────────────────────────────────────
        private const double SweepUseFraction = 0.80;

        private void StartSweep(CancellationToken token, bool fullRange)
        {
            double startMhz, stopMhz;

            if (fullRange)
            {
                startMhz = FreqMinMhz;
                stopMhz  = FreqMaxMhz;
            }
            else
            {
                if (!TryParseFreqMhz(TxtStartFreq.Text, out startMhz)) startMhz = FreqMinMhz;
                if (!TryParseFreqMhz(TxtStopFreq.Text,  out stopMhz))  stopMhz  = FreqMaxMhz;
                if (startMhz >= stopMhz)
                {
                    TxtStatus.Text = "Start freq must be less than stop freq.";
                    StopAcquisition();
                    return;
                }
            }

            double bwMhz      = _sampleRateHz / 1e6;
            int    usedBins   = (int)(_fftSize * SweepUseFraction);
            double usedBwMhz  = bwMhz * SweepUseFraction;

            // LO must stay inside hardware limits and ±bw/2 from each edge
            double loMin = Math.Max(startMhz, FreqMinMhz + bwMhz / 2.0);
            double loMax = Math.Min(stopMhz,  FreqMaxMhz - bwMhz / 2.0);

            if (loMin >= loMax)
            {
                TxtStatus.Text = "Span too wide for selected frequency range.";
                StopAcquisition();
                return;
            }

            int    steps     = (int)Math.Ceiling((loMax - loMin) / usedBwMhz) + 1;
            int    totalBins = steps * usedBins;

            _sweepFreq    = new double[totalBins];
            _sweepPowerDb = new double[totalBins];
            _sweepPsdDb   = new double[totalBins];

            int    binSkip = (_fftSize - usedBins) / 2;
            double binHz   = (double)_sampleRateHz / _fftSize;
            for (int s = 0; s < steps; s++)
            {
                double fcMhz = Math.Min(loMin + s * usedBwMhz, loMax);
                for (int b = 0; b < usedBins; b++)
                {
                    int    fftBin  = binSkip + b;
                    double freqMhz = fcMhz + (fftBin - _fftSize / 2) * binHz / 1e6;
                    _sweepFreq[s * usedBins + b] = freqMhz;
                }
            }

            ResetWaterfall(totalBins, startMhz, stopMhz);

            Dispatcher.Invoke(() =>
            {
                SpectrumPlot.Plot.Axes.SetLimitsX(startMhz, stopMhz);
                PsdPlot.Plot.Axes.SetLimitsX(startMhz, stopMhz);

                _sweepSpecPlot           = SpectrumPlot.Plot.Add.SignalXY(_sweepFreq, _sweepPowerDb);
                _sweepSpecPlot.Color     = Color.FromHex("#89B4FA");
                _sweepSpecPlot.LineWidth = 1;

                _sweepPsdPlot            = PsdPlot.Plot.Add.SignalXY(_sweepFreq, _sweepPsdDb);
                _sweepPsdPlot.Color      = Color.FromHex("#A6E3A1");
                _sweepPsdPlot.LineWidth  = 1;

                UpdateStatusBar();
            });

            Task.Run(() => SweepLoop(steps, usedBins, binSkip, totalBins,
                                     loMin, loMax, startMhz, stopMhz, token), token);
        }

        private void SweepLoop(int steps, int usedBins, int binSkip, int totalBins,
                                double loMin, double loMax,
                                double displayStart, double displayStop,
                                CancellationToken token)
        {
            double usedBwMhz = _sampleRateHz / 1e6 * SweepUseFraction;
            var    sweepRow  = new double[totalBins];
            while (!token.IsCancellationRequested && _sdr != null)
            {
                int goodSteps = 0;

                for (int s = 0; s < steps && !token.IsCancellationRequested; s++)
                {
                    double fcMhz = Math.Clamp(loMin + s * usedBwMhz, loMin, loMax);
                    long   fcHz  = (long)(fcMhz * 1e6);

                    try
                    {
                        _sdr.SetCenterFreq(fcHz);
                        Thread.Sleep(12);
                        _sdr.ReadSamples();   // discard stale buffer
                        ComputeSpectrumAndPsd(_sdr.ReadSamples(), out double[] spec, out double[] psd);

                        int offset = s * usedBins;
                        Array.Copy(spec, binSkip, _sweepPowerDb, offset, usedBins);
                        Array.Copy(psd,  binSkip, _sweepPsdDb,   offset, usedBins);
                        Array.Copy(spec, binSkip, sweepRow,      offset, usedBins);
                        goodSteps++;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => TxtStatus.Text = $"Sweep step {s} error: {ex.Message}");
                    }

                    if (s % 16 == 0)
                        Dispatcher.Invoke(() => { SpectrumPlot.Refresh(); PsdPlot.Refresh(); });
                }

                if (goodSteps == 0) break;

                PushWaterfallRow(sweepRow);
                Dispatcher.Invoke(() =>
                {
                    _wfHeatmap?.Update();
                    SpectrumPlot.Refresh();
                    PsdPlot.Refresh();
                    WaterfallPlot.Refresh();
                    TxtStatus.Text = $"Sweep {displayStart:F1}–{displayStop:F1} MHz — {DateTime.Now:HH:mm:ss}  ({goodSteps}/{steps} steps)";
                });
            }
        }

        // ── Waterfall helpers ────────────────────────────────────────────────
        private void ResetWaterfall(int bins, double freqMin, double freqMax)
        {
            _wfBins = bins;
            _wfMatrix = new double[WaterfallRows, bins];

            Dispatcher.Invoke(() =>
            {
                WaterfallPlot.Plot.Clear();
                _wfHeatmap = null;

                _wfHeatmap = WaterfallPlot.Plot.Add.Heatmap(_wfMatrix);
                _wfHeatmap.Colormap    = new Turbo();
                _wfHeatmap.Position    = new ScottPlot.CoordinateRect(
                                                freqMin, freqMax, 0, WaterfallRows);
                _wfHeatmap.ManualRange = new ScottPlot.Range(-120, 0);

                WaterfallPlot.Plot.Axes.SetLimitsX(freqMin, freqMax);
                WaterfallPlot.Plot.Axes.SetLimitsY(0, WaterfallRows);
                WaterfallPlot.Plot.Axes.Left.TickGenerator =
                    new ScottPlot.TickGenerators.EmptyTickGenerator();

                WaterfallPlot.Refresh();
            });
        }

        private void PushWaterfallRow(double[] row)
        {
            int bins = Math.Min(row.Length, _wfBins);

            for (int r = WaterfallRows - 1; r > 0; r--)
                Buffer.BlockCopy(_wfMatrix, (r - 1) * _wfBins * sizeof(double),
                                 _wfMatrix, r       * _wfBins * sizeof(double),
                                 _wfBins * sizeof(double));

            for (int b = 0; b < bins; b++)
                _wfMatrix[0, b] = row[b];
        }

        // ── DSP ──────────────────────────────────────────────────────────────
        private void ComputeSpectrumAndPsd(short[] iq, out double[] spec, out double[] psd)
        {
            int    n   = _fftSize;
            double fs  = _sampleRateHz;
            double rbw = fs / n;

            var    samples = new System.Numerics.Complex[n];
            double wSum2   = 0;

            for (int i = 0; i < n; i++)
            {
                double w  = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
                wSum2    += w * w;
                samples[i] = new System.Numerics.Complex(
                    iq[i * 2]     / 32768.0 * w,
                    iq[i * 2 + 1] / 32768.0 * w);
            }

            Fourier.Forward(samples, FourierOptions.Matlab);

            spec = new double[n];
            psd  = new double[n];

            double normSpec = 1.0 / n;
            double normPsd  = 1.0 / (wSum2 * rbw);

            for (int i = 0; i < n; i++)
            {
                int    k   = (i + n / 2) % n;
                double mag = samples[k].Magnitude;
                spec[i] = 20 * Math.Log10(Math.Max(mag * normSpec, 1e-12)) + _calOffsetDb;
                psd[i]  = 10 * Math.Log10(Math.Max(mag * mag * normPsd, 1e-24)) + _calOffsetDb;
            }
        }

        private void AccumulateAverage(double[] spec, double[] psd, int avgLen)
        {
            if (_avgSpecBuf == null || _avgSpecBuf.Length != _fftSize)
            {
                _avgSpecBuf = new double[_fftSize];
                _avgPsdBuf  = new double[_fftSize];
                _avgCount   = 0;
            }

            if (_avgCount < avgLen)
            {
                for (int i = 0; i < _fftSize; i++)
                {
                    _avgSpecBuf![i] = (_avgSpecBuf[i] * _avgCount + spec[i]) / (_avgCount + 1);
                    _avgPsdBuf![i]  = (_avgPsdBuf[i]  * _avgCount + psd[i])  / (_avgCount + 1);
                }
                _avgCount++;
            }
            else
            {
                double alpha = 1.0 / avgLen;
                for (int i = 0; i < _fftSize; i++)
                {
                    _avgSpecBuf![i] = _avgSpecBuf[i] * (1 - alpha) + spec[i] * alpha;
                    _avgPsdBuf![i]  = _avgPsdBuf[i]  * (1 - alpha) + psd[i]  * alpha;
                }
            }

            Array.Copy(_avgSpecBuf!, _powerDb, _fftSize);
            Array.Copy(_avgPsdBuf!,  _psdDb,   _fftSize);
        }

        // ── Status bar ───────────────────────────────────────────────────────
        private void UpdateStatusBar()
        {
            string mode = GetMode();
            if (mode == "live")
            {
                int avgDepth = VbwToAvgDepth(_rbwHz, _vbwHz);
                TxtStatus.Text = $"Live — {_centerFreqHz / 1e6:F3} MHz  |  Fs = {FormatHz(_sampleRateHz)}  |  FFT = {_fftSize}  |  Avg = {avgDepth}";
                TxtSaInfo.Text = $"RBW {FormatHz(_rbwHz)}  •  VBW {FormatHz(_vbwHz)}  •  Span {FormatHz(_spanHz)}";
            }
            else
            {
                TxtSaInfo.Text = $"RBW {FormatHz(_rbwHz)}  •  VBW {FormatHz(_vbwHz)}  •  Step {FormatHz((long)(_sampleRateHz * SweepUseFraction))}";
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private void PopulateRxAntennaPorts(string[] ports)
        {
            CmbRxAntenna.Items.Clear();
            foreach (string port in ports)
            {
                string label = port == "A_BALANCED" ? "Antenna 1"
                             : port == "B_BALANCED" ? "Antenna 2"
                             : port;
                var item = new ComboBoxItem { Content = label, Tag = port };
                CmbRxAntenna.Items.Add(item);
            }
            if (CmbRxAntenna.Items.Count > 0)
                CmbRxAntenna.SelectedIndex = 0;
        }

        private string GetMode()
        {
            if (CmbMode?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "live";
        }

        private static bool TryParseFreqMhz(string text, out double mhz)
        {
            mhz = 0;
            return double.TryParse(text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out mhz) && mhz >= FreqMinMhz && mhz <= FreqMaxMhz;
        }

        private static long SelectedLongTag(ComboBox cmb, long fallback)
        {
            if (cmb?.SelectedItem is ComboBoxItem item &&
                item.Tag is string s &&
                long.TryParse(s, out long v))
                return v;
            return fallback;
        }
    }
}
