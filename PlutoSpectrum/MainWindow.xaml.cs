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
        // _freqMaxMhz is configurable at runtime via TxtMaxFreq.
        // Default is 6000 MHz (standard firmware). Extended firmware supports ~6999 MHz.
        private double       _freqMaxMhz  = PlutoSdr.LoMaxHz / 1e6;
        private const int    WaterfallRows = 200;

        // M1 fix: volatile so background thread always sees UI-thread writes promptly.
        private volatile PlutoSdr? _sdr;
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
        // H2 fix: lock protecting _wfMatrix and _wfBins shared between UI and background threads.
        private readonly object _wfLock = new();

        // ── FFT backend ──────────────────────────────────────────────────────
        private IFftProvider _fftProvider = new CpuFftProvider();   // replaced at startup

        // ── Bottom view ──────────────────────────────────────────────────────
        private string _bottomView = "waterfall";  // waterfall | psd | iq | none

        // ── IQ Constellation ────────────────────────────────────────────────
        private double[] _iqI = [];
        private double[] _iqQ = [];
        private ScottPlot.Plottables.Scatter? _iqScatter;

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
        // M1 fix: use Interlocked with bit-cast so background thread always sees the latest
        // value written by the UI thread. volatile is illegal on double in C#.
        private long _calOffsetDbBits = 0L;
        private double _calOffsetDb
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _calOffsetDbBits));
            set => Interlocked.Exchange(ref _calOffsetDbBits, BitConverter.DoubleToInt64Bits(value));
        }
        private double _refLevelDb  = 0.0;   // top of Y axis (dBFS + offset)
        // W1 note: _dbPerDiv stores total Y range = (dB/div × 10 divisions). Not per-division.
        private int    _dbTotalRange = 100;

        // ────────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            ConfigurePlots();
            _plotsReady = true;
            // W6 fix: remove redundant constructor lambda — XAML already wires SldGain_ValueChanged.
            ParseMaxFreq();         // sync _freqMaxMhz from the XAML default in TxtMaxFreq
            UpdateDerivedReadouts();
            ApplyYAxisLimits();

            // Select FFT backend: GPU if a discrete accelerator is found, CPU otherwise.
            _fftProvider = FftProviderFactory.Create(out string fftDiag);
            TxtStatus.Text = fftDiag;
        }

        // ── Plot setup ───────────────────────────────────────────────────────
        private void ConfigurePlots()
        {
            StyleLinePlot(SpectrumPlot.Plot, "Frequency (MHz)", "Power (dBFS)", -100, 0);
            StyleWaterfallPlot(SecondaryPlot.Plot);   // default view is waterfall

            SpectrumPlot.UserInputProcessor.IsEnabled   = true;
            SecondaryPlot.UserInputProcessor.IsEnabled  = true;

            SpectrumPlot.MouseMove  += PlotMouseMove;
            SecondaryPlot.MouseMove += PlotMouseMove;
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
            // W4 fix: set tick generator once here instead of on every ResetWaterfall call.
            plt.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.EmptyTickGenerator();
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
            if (_sdr != null && _running)
            {
                try { _sdr.SetGain(e.NewValue); }
                catch (Exception ex)
                {
                    TxtStatus.Text = $"Gain write failed: {ex.Message}";
                }
            }
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
            _dbTotalRange = (int)SelectedLongTag(CmbDbDiv, 100);
            ApplyYAxisLimits();
        }

        private void TxtRefLevel_LostFocus(object sender, RoutedEventArgs e)  => ParseRefLevel();
        private void TxtCalOffset_LostFocus(object sender, RoutedEventArgs e) => ParseCalOffset();
        private void TxtMaxFreq_LostFocus(object sender, RoutedEventArgs e)   => ParseMaxFreq();
        private void TxtMaxFreq_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ParseMaxFreq();
        }

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
                UpdateRightPanel();
            }
        }

        private void ParseMaxFreq()
        {
            if (double.TryParse(TxtMaxFreq.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)
                && v > FreqMinMhz && v <= PlutoSdr.LoMaxHzExtended / 1e6)
            {
                _freqMaxMhz = v;
                // Update the Stop field default to not exceed the new max
                if (double.TryParse(TxtStopFreq.Text, out double stop) && stop > _freqMaxMhz)
                    TxtStopFreq.Text = _freqMaxMhz.ToString("F0");
            }
            else
            {
                // Reset to current value on bad input
                TxtMaxFreq.Text = _freqMaxMhz.ToString("F0");
            }
        }

        // Recomputes Y-axis limits and label from ref level, dB/div and cal offset.
        // Called whenever any of those three change; safe to call while running.
        private void ApplyYAxisLimits()
        {
            if (!_plotsReady) return;
            double top    = _refLevelDb + _calOffsetDb;
            double bottom = top - _dbTotalRange;
            string yLabel = _calOffsetDb != 0.0 ? "Power (dBm est.)" : "Power (dBFS)";
            string pLabel = _calOffsetDb != 0.0 ? "PSD (dBm/Hz est.)" : "PSD (dBFS/Hz)";

            void Apply()
            {
                SpectrumPlot.Plot.Axes.SetLimitsY(bottom, top);
                SpectrumPlot.Plot.Axes.Left.Label.Text = yLabel;
                SpectrumPlot.Refresh();

                if (_bottomView == "psd")
                {
                    SecondaryPlot.Plot.Axes.SetLimitsY(bottom - 80, top - 20);
                    SecondaryPlot.Plot.Axes.Left.Label.Text = pLabel;
                    SecondaryPlot.Refresh();
                }
                else if (_bottomView == "waterfall" && _wfHeatmap != null)
                {
                    _wfHeatmap.ManualRange = new ScottPlot.Range(bottom, top);
                    SecondaryPlot.Refresh();
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
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

        private void CmbBottomView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBottomView?.SelectedItem is not ComboBoxItem item) return;
            string view = item.Tag?.ToString() ?? "waterfall";
            ApplyBottomView(view);
        }

        private void ApplyBottomView(string view)
        {
            if (!_plotsReady) { _bottomView = view; return; }
            _bottomView = view;

            // Show/hide the secondary panel row
            bool visible = view != "none";
            if (SecondaryBorder != null)
                SecondaryBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (SecondaryRow != null)
                SecondaryRow.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            if (!visible) return;

            // Reconfigure SecondaryPlot for the selected view
            SecondaryPlot.Plot.Clear();
            _psdPlot = null;
            _sweepPsdPlot = null;
            _wfHeatmap = null;
            _iqScatter = null;

            switch (view)
            {
                case "waterfall":
                    StyleWaterfallPlot(SecondaryPlot.Plot);
                    TxtSecondaryLabel.Text       = "WATERFALL";
                    TxtSecondaryLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentRed");
                    if (_running)
                    {
                        string wfMode = GetMode();
                        int    wfBins  = wfMode == "live" ? _fftSize          : _sweepFreq.Length;
                        double wfStart = wfMode == "live" ? _freqAxis[0]      : (_sweepFreq.Length > 0 ? _sweepFreq[0]  : 0);
                        double wfStop  = wfMode == "live" ? _freqAxis[^1]     : (_sweepFreq.Length > 0 ? _sweepFreq[^1] : 1);
                        if (wfBins > 0) ResetWaterfall(wfBins, wfStart, wfStop);
                    }
                    break;

                case "psd":
                {
                    double top    = _refLevelDb + _calOffsetDb;
                    double bottom = top - _dbTotalRange;
                    string pLabel = _calOffsetDb != 0.0 ? "PSD (dBm/Hz est.)" : "PSD (dBFS/Hz)";
                    StyleLinePlot(SecondaryPlot.Plot, "Frequency (MHz)", pLabel, bottom - 80, top - 20);
                    TxtSecondaryLabel.Text       = "PSD";
                    TxtSecondaryLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
                    if (_running)
                    {
                        string mode = GetMode();
                        if (mode == "live" && _freqAxis.Length > 0)
                        {
                            _psdPlot            = SecondaryPlot.Plot.Add.SignalXY(_freqAxis, _psdDb);
                            _psdPlot.Color      = ScottPlot.Color.FromHex("#A6E3A1");
                            _psdPlot.LineWidth  = 1;
                            SecondaryPlot.Plot.Axes.SetLimitsX(_freqAxis[0], _freqAxis[^1]);
                        }
                        else if ((mode == "sweep" || mode == "fullsweep") && _sweepFreq.Length > 0)
                        {
                            _sweepPsdPlot            = SecondaryPlot.Plot.Add.SignalXY(_sweepFreq, _sweepPsdDb);
                            _sweepPsdPlot.Color      = ScottPlot.Color.FromHex("#A6E3A1");
                            _sweepPsdPlot.LineWidth  = 1;
                            SecondaryPlot.Plot.Axes.SetLimitsX(_sweepFreq[0], _sweepFreq[^1]);
                        }
                    }
                    SecondaryPlot.Refresh();
                    break;
                }

                case "iq":
                    StyleIqPlot(SecondaryPlot.Plot);
                    TxtSecondaryLabel.Text       = "IQ CONSTELLATION";
                    TxtSecondaryLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlue");
                    if (_iqI.Length > 0)
                    {
                        _iqScatter = SecondaryPlot.Plot.Add.Scatter(_iqI, _iqQ);
                        _iqScatter.Color      = ScottPlot.Color.FromHex("#CBA6F7");
                        _iqScatter.LineWidth  = 0;
                        _iqScatter.MarkerSize = 2;
                    }
                    SecondaryPlot.Refresh();
                    break;
            }
        }

        private static readonly double[] IqTicks      = [-1.0, -0.75, -0.5, -0.25, 0.0, 0.25, 0.5, 0.75, 1.0];
        private static readonly string[] IqTickLabels = ["-1", "-.75", "-.5", "-.25", "0", ".25", ".5", ".75", "1"];

        private static void StyleIqPlot(ScottPlot.Plot plt)
        {
            plt.FigureBackground.Color      = Color.FromHex("#1E1E2E");
            plt.DataBackground.Color        = Color.FromHex("#181825");
            plt.Axes.Color(Color.FromHex("#CDD6F4"));
            plt.Axes.Bottom.Label.Text      = "I";
            plt.Axes.Left.Label.Text        = "Q";
            plt.Axes.Bottom.Label.ForeColor = Color.FromHex("#89B4FA");
            plt.Axes.Left.Label.ForeColor   = Color.FromHex("#CBA6F7");
            plt.Axes.SetLimitsY(-1.1, 1.1);
            plt.Axes.SetLimitsX(-1.1, 1.1);

            // Fixed ticks so grid lines land on clean decimal values
            plt.Axes.Bottom.SetTicks(IqTicks, IqTickLabels);
            plt.Axes.Left.SetTicks(IqTicks, IqTickLabels);

            // Major grid — brighter lines for the fixed ticks
            plt.Grid.MajorLineColor   = Color.FromHex("#45475A");
            plt.Grid.MajorLineWidth   = 1;
            plt.Grid.MajorLinePattern = LinePattern.Solid;

            // Zero-axis crosshairs in a distinct colour
            plt.Add.HorizontalLine(0, 1, Color.FromHex("#6C7086"), LinePattern.Solid);
            plt.Add.VerticalLine(0,   1, Color.FromHex("#6C7086"), LinePattern.Solid);

            // Unit-circle reference — marks the full-scale boundary
            var circle = plt.Add.Circle(0, 0, 1);
            circle.FillColor = Colors.Transparent;
            circle.LineColor = Color.FromHex("#585B70");
            circle.LineWidth = 1;
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

        private void Window_Closed(object sender, EventArgs e)
        {
            StopAcquisition();
            _fftProvider.Dispose();
        }

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
            ResolveParameters();   // side-effect: validates combos; results shown via UpdateRightPanel
            UpdateRightPanel();
        }

        private static string FormatHz(long hz)
        {
            if (hz >= 1_000_000) return $"{hz / 1_000_000.0:G3} MHz";
            if (hz >= 1_000)     return $"{hz / 1000.0:G3} kHz";
            return $"{hz} Hz";
        }

        // ── Acquisition dispatch ─────────────────────────────────────────────
        private async void StartAcquisition()
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
                    TxtStatus.Text = $"Invalid center frequency — enter {FreqMinMhz}–{_freqMaxMhz} MHz.";
                    return;
                }
                openFreq = (long)(mhz * 1e6);
            }
            else
            {
                // Sweep modes: open at start freq; report parse failure to status bar (M9 fix)
                if (!TryParseFreqMhz(TxtStartFreq.Text, out double startMhz))
                {
                    TxtStatus.Text = $"Invalid start frequency — enter {FreqMinMhz}–{_freqMaxMhz} MHz.";
                    return;
                }
                openFreq = (long)(startMhz * 1e6);
            }

            TxtStatus.Text = $"Connecting… fc={openFreq/1e6:F3} MHz  fs={_sampleRateHz/1e6:F3} MSPS  RBW={FormatHz(_rbwHz)}";
            BtnStartStop.IsEnabled = false;

            string rxAnt = (CmbRxAntenna.SelectedItem as ComboBoxItem)?.Tag as string ?? "A_BALANCED";

            // Pass the user's desired max only if it exceeds 6 GHz (opt-in to extended firmware).
            // For standard firmware the hardware rejects anything above 6 GHz with EINVAL.
            long userMaxHz = (long)(_freqMaxMhz * 1e6);
            long loMaxHz   = userMaxHz > PlutoSdr.LoMaxHz ? PlutoSdr.LoMaxHzExtended : PlutoSdr.LoMaxHz;

            // Capture locals for the background task — no UI access allowed inside Task.Run.
            long   capturedFs      = _sampleRateHz;
            long   capturedRfBw    = _rfBwHz;
            double capturedGain    = SldGain.Value;
            int    capturedFftSize = _fftSize;

            var sdr = new PlutoSdr();
            Exception? openError = null;
            await Task.Run(() =>
            {
                try { sdr.Open(openFreq, capturedFs, capturedRfBw, capturedGain, capturedFftSize, ip, rxAnt, loMaxHz); }
                catch (Exception ex) { openError = ex; }
            });

            BtnStartStop.IsEnabled = true;

            if (openError != null)
            {
                TxtStatus.Text = $"Error: {openError.Message}";
                // M7 fix: only read RxPortsAvailable when the SDR was open enough to probe ports.
                if (sdr.RxPortsAvailable.Length > 0)
                    PopulateRxAntennaPorts(sdr.RxPortsAvailable);
                sdr.Dispose();
                return;
            }

            // C2 fix: assign _sdr only after Open() succeeds, so the background thread
            // never sees a partially-initialised object.
            _sdr = sdr;
            // Sync the runtime max frequency from the SDR (may differ from user input
            // if the firmware clamped it). This keeps sweep calculations consistent.
            _freqMaxMhz = sdr.ActualLoMaxHz / 1e6;

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
            // C1 fix: StopAcquisition is always called from the UI thread.
            // Do not use Dispatcher.Invoke here — we ARE the UI thread; calling Invoke
            // while the background thread has a pending Invoke causes a deadlock.
            _running = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // C2 fix: null _sdr before Dispose so any racing background read gets null,
            // not a disposing object. Dispose is idempotent.
            var sdrToDispose = _sdr;
            _sdr = null;
            sdrToDispose?.Dispose();

            BtnStartStop.Content   = "▶  START";
            BtnStartStop.Style     = (Style)FindResource("StartButton");
            // W3 fix: disable Apply while stopped — it does nothing when not running.
            BtnApplyFreq.IsEnabled = false;
            TxtStatus.Text         = "Stopped.";
            TxtSaInfo.Text         = "";
            SpectrumPlot.Plot.Clear();
            SecondaryPlot.Plot.Clear();
            _specPlot = _psdPlot = _sweepSpecPlot = _sweepPsdPlot = null;
            _wfHeatmap = null;
            _iqScatter = null;
            SpectrumPlot.Refresh();
            SecondaryPlot.Refresh();
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

            _specPlot           = SpectrumPlot.Plot.Add.SignalXY(_freqAxis, _powerDb);
            _specPlot.Color     = Color.FromHex("#89B4FA");
            _specPlot.LineWidth = 1;

            if (_bottomView == "waterfall")
                ResetWaterfall(_fftSize, _freqAxis[0], _freqAxis[^1]);
            else if (_bottomView == "psd")
            {
                SecondaryPlot.Plot.Clear();
                _psdPlot            = SecondaryPlot.Plot.Add.SignalXY(_freqAxis, _psdDb);
                _psdPlot.Color      = Color.FromHex("#A6E3A1");
                _psdPlot.LineWidth  = 1;
                SecondaryPlot.Plot.Axes.SetLimitsX(_freqAxis[0], _freqAxis[^1]);
            }
            else if (_bottomView == "iq")
            {
                _iqI = new double[_fftSize];
                _iqQ = new double[_fftSize];
                SecondaryPlot.Plot.Clear();
                _iqScatter = SecondaryPlot.Plot.Add.Scatter(_iqI, _iqQ);
                _iqScatter.Color      = Color.FromHex("#CBA6F7");
                _iqScatter.LineWidth  = 0;
                _iqScatter.MarkerSize = 2;
            }

            UpdateStatusBar();

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    // C2 fix: capture _sdr into a local so the null-check and all subsequent
                    // uses are on the same reference, preventing a race with StopAcquisition.
                    var sdr = _sdr;
                    if (sdr == null) break;

                    try
                    {
                        short[] raw = sdr.ReadSamples();
                        int avgDepth = VbwToAvgDepth(_rbwHz, _vbwHz);
                        ComputeSpectrumAndPsd(raw, out double[] spec, out double[] psd);
                        AccumulateAverage(spec, psd, avgDepth);

                        string view = _bottomView;
                        if (view == "waterfall")
                            PushWaterfallRow(_powerDb);
                        else if (view == "iq")
                            UpdateIqBufferFromRaw(raw);

                        // C1 fix: BeginInvoke (fire-and-forget) so the background thread never
                        // blocks the dispatcher queue while the UI thread is in StopAcquisition.
                        Dispatcher.BeginInvoke(() =>
                        {
                            SpectrumPlot.Refresh();
                            if (_bottomView == "waterfall") { _wfHeatmap?.Update(); SecondaryPlot.Refresh(); }
                            else if (_bottomView == "psd" || _bottomView == "iq") { SecondaryPlot.Refresh(); }
                        });
                        // Pace the loop to ~30 fps so BeginInvoke callbacks drain between frames.
                        Thread.Sleep(33);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // M6 fix: report error and stop cleanly instead of silently breaking.
                        Dispatcher.BeginInvoke(() =>
                        {
                            TxtStatus.Text = $"Acquisition error: {ex.Message}";
                            if (_running) StopAcquisition();
                        });
                        break;
                    }
                }
            }, token);
        }

        private void UpdateLiveFreqAxis()
        {
            int    n    = _fftSize;
            double fc   = _centerFreqHz / 1e6;
            double bw   = _sampleRateHz / 1e6;
            double step = bw / n;

            var newAxis = new double[n];
            for (int i = 0; i < n; i++)
                newAxis[i] = fc - bw / 2 + i * step;

            _freqAxis = newAxis;
            _powerDb  = new double[n];
            _psdDb    = new double[n];

            // H1 fix: rebuild the SignalXY plots with the new arrays so the plot reference
            // and the freq/data arrays always stay in sync after Apply.
            if (_specPlot != null || _psdPlot != null || _iqScatter != null)
            {
                SpectrumPlot.Plot.Clear();
                _specPlot           = SpectrumPlot.Plot.Add.SignalXY(_freqAxis, _powerDb);
                _specPlot.Color     = Color.FromHex("#89B4FA");
                _specPlot.LineWidth = 1;

                if (_bottomView == "psd")
                {
                    SecondaryPlot.Plot.Clear();
                    _psdPlot            = SecondaryPlot.Plot.Add.SignalXY(_freqAxis, _psdDb);
                    _psdPlot.Color      = Color.FromHex("#A6E3A1");
                    _psdPlot.LineWidth  = 1;
                    SecondaryPlot.Plot.Axes.SetLimitsX(_freqAxis[0], _freqAxis[n - 1]);
                }
                else if (_bottomView == "iq")
                {
                    _iqI = new double[n];
                    _iqQ = new double[n];
                    SecondaryPlot.Plot.Clear();
                    _iqScatter = SecondaryPlot.Plot.Add.Scatter(_iqI, _iqQ);
                    _iqScatter.Color      = Color.FromHex("#CBA6F7");
                    _iqScatter.LineWidth  = 0;
                    _iqScatter.MarkerSize = 2;
                }
            }

            // Always called from the UI thread (StartLive / BtnApplyFreq_Click) — no Invoke needed.
            SpectrumPlot.Plot.Axes.SetLimitsX(_freqAxis[0], _freqAxis[n - 1]);
        }

        // ── Sweep mode ───────────────────────────────────────────────────────
        private const double SweepUseFraction = 0.80;

        private void StartSweep(CancellationToken token, bool fullRange)
        {
            double startMhz, stopMhz;

            if (fullRange)
            {
                startMhz = FreqMinMhz;
                stopMhz  = _freqMaxMhz;
            }
            else
            {
                if (!TryParseFreqMhz(TxtStartFreq.Text, out startMhz))
                {
                    TxtStatus.Text = $"Invalid start frequency — enter {FreqMinMhz}–{_freqMaxMhz} MHz.";
                    StopAcquisition();
                    return;
                }
                if (!TryParseFreqMhz(TxtStopFreq.Text, out stopMhz))
                {
                    TxtStatus.Text = $"Invalid stop frequency — enter {FreqMinMhz}–{_freqMaxMhz} MHz.";
                    StopAcquisition();
                    return;
                }
                if (startMhz >= stopMhz)
                {
                    TxtStatus.Text = "Start freq must be less than stop freq.";
                    StopAcquisition();
                    return;
                }
            }

            double bwMhz     = _sampleRateHz / 1e6;
            // M9 fix: round usedBins symmetrically so the skip is equal on both edges.
            // (int)(_fftSize * 0.80) truncates; rounding avoids a one-bin asymmetry for
            // FFT sizes 1024, 8192, 16384.
            int    usedBins  = (int)Math.Round(_fftSize * SweepUseFraction);
            // Force even so (fftSize - usedBins) is always divisible by 2.
            if (usedBins % 2 != 0) usedBins--;
            double usedBwMhz = bwMhz * usedBins / _fftSize;

            // LO must stay inside hardware limits and ±bw/2 from each edge
            double loMin = Math.Max(startMhz, FreqMinMhz + bwMhz / 2.0);
            // M8 fix: clamp loMax so the used window never extends past stopMhz.
            double loMax = Math.Min(stopMhz - usedBwMhz / 2.0, _freqMaxMhz - bwMhz / 2.0);

            if (loMin >= loMax)
            {
                TxtStatus.Text = "Span too wide or too narrow for selected frequency range.";
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

            // StartSweep is called from the UI thread — run directly, no Dispatcher.Invoke needed.
            SpectrumPlot.Plot.Axes.SetLimitsX(startMhz, stopMhz);

            _sweepSpecPlot           = SpectrumPlot.Plot.Add.SignalXY(_sweepFreq, _sweepPowerDb);
            _sweepSpecPlot.Color     = Color.FromHex("#89B4FA");
            _sweepSpecPlot.LineWidth = 1;

            if (_bottomView == "waterfall")
            {
                ResetWaterfall(totalBins, startMhz, stopMhz);
            }
            else if (_bottomView == "psd")
            {
                SecondaryPlot.Plot.Axes.SetLimitsX(startMhz, stopMhz);
                _sweepPsdPlot            = SecondaryPlot.Plot.Add.SignalXY(_sweepFreq, _sweepPsdDb);
                _sweepPsdPlot.Color      = Color.FromHex("#A6E3A1");
                _sweepPsdPlot.LineWidth  = 1;
            }

            UpdateStatusBar();

            Task.Run(() => SweepLoop(steps, usedBins, binSkip, totalBins,
                                     loMin, loMax, startMhz, stopMhz, token), token);
        }

        private void SweepLoop(int steps, int usedBins, int binSkip, int totalBins,
                                double loMin, double loMax,
                                double displayStart, double displayStop,
                                CancellationToken token)
        {
            double usedBwMhz = _sampleRateHz / 1e6 * usedBins / _fftSize;
            var    sweepRow  = new double[totalBins];

            // H4 fix: sweep averaging state, independent from live-mode averaging buffers.
            double[]? avgSpecBuf = null;
            double[]? avgPsdBuf  = null;
            int       avgCount   = 0;

            while (!token.IsCancellationRequested)
            {
                // C2 fix: capture _sdr once per pass; if it became null, exit cleanly.
                var sdr = _sdr;
                if (sdr == null) break;

                int goodSteps = 0;

                // H6 fix: zero sweepRow at the start of every pass so failed steps
                // never leave stale data from a prior pass in the waterfall row.
                Array.Clear(sweepRow, 0, totalBins);

                int avgDepth = VbwToAvgDepth(_rbwHz, _vbwHz);

                for (int s = 0; s < steps && !token.IsCancellationRequested; s++)
                {
                    double fcMhz = Math.Clamp(loMin + s * usedBwMhz, loMin, loMax);
                    long   fcHz  = (long)(fcMhz * 1e6);

                    try
                    {
                        sdr.SetCenterFreq(fcHz);
                        Thread.Sleep(12);
                        sdr.ReadSamples();   // discard stale buffer
                        ComputeSpectrumAndPsd(sdr.ReadSamples(), out double[] spec, out double[] psd);

                        // H4 fix: per-step averaging so VBW actually affects sweep output.
                        if (avgSpecBuf == null || avgSpecBuf.Length != usedBins)
                        {
                            avgSpecBuf = new double[usedBins];
                            avgPsdBuf  = new double[usedBins];
                            avgCount   = 0;
                        }

                        var specSlice = new double[usedBins];
                        var psdSlice  = new double[usedBins];
                        Array.Copy(spec, binSkip, specSlice, 0, usedBins);
                        Array.Copy(psd,  binSkip, psdSlice,  0, usedBins);

                        if (avgCount < avgDepth)
                        {
                            for (int i = 0; i < usedBins; i++)
                            {
                                avgSpecBuf[i] = (avgSpecBuf[i] * avgCount + specSlice[i]) / (avgCount + 1);
                                avgPsdBuf![i] = (avgPsdBuf[i]  * avgCount + psdSlice[i])  / (avgCount + 1);
                            }
                            avgCount++;
                        }
                        else
                        {
                            double alpha = 1.0 / avgDepth;
                            for (int i = 0; i < usedBins; i++)
                            {
                                avgSpecBuf[i] = avgSpecBuf[i] * (1 - alpha) + specSlice[i] * alpha;
                                avgPsdBuf![i] = avgPsdBuf[i]  * (1 - alpha) + psdSlice[i]  * alpha;
                            }
                        }

                        int offset = s * usedBins;
                        Array.Copy(avgSpecBuf, 0, _sweepPowerDb, offset, usedBins);
                        Array.Copy(avgPsdBuf!,  0, _sweepPsdDb,   offset, usedBins);
                        Array.Copy(avgSpecBuf, 0, sweepRow,      offset, usedBins);
                        goodSteps++;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // C1 fix: BeginInvoke so this never deadlocks against StopAcquisition.
                        // W5 fix: only refresh on success cadence, not on every failed step.
                        Dispatcher.BeginInvoke(() => TxtStatus.Text = $"Sweep step {s} error: {ex.Message}");
                    }

                    // W5 fix: only trigger partial refresh when the step succeeded (goodSteps advanced).
                    if (s % 16 == 0 && goodSteps > 0)
                        Dispatcher.BeginInvoke(() => { SpectrumPlot.Refresh(); SecondaryPlot.Refresh(); });
                }

                if (goodSteps == 0) break;

                if (_bottomView == "waterfall") PushWaterfallRow(sweepRow);
                // C1 fix: BeginInvoke throughout sweep loop.
                Dispatcher.BeginInvoke(() =>
                {
                    SpectrumPlot.Refresh();
                    if (_bottomView == "waterfall") { _wfHeatmap?.Update(); SecondaryPlot.Refresh(); }
                    else if (_bottomView == "psd")  { SecondaryPlot.Refresh(); }
                    TxtStatus.Text = $"Sweep {displayStart:F1}–{displayStop:F1} MHz — {DateTime.Now:HH:mm:ss}  ({goodSteps}/{steps} steps)";
                });
            }
        }

        // ── Waterfall helpers ────────────────────────────────────────────────
        private void ResetWaterfall(int bins, double freqMin, double freqMax)
        {
            // H2 fix: update _wfBins and _wfMatrix together under the lock so
            // PushWaterfallRow (background thread) always sees a consistent pair.
            double[,] newMatrix;
            lock (_wfLock)
            {
                _wfBins   = bins;
                newMatrix = new double[WaterfallRows, bins];
                _wfMatrix = newMatrix;
            }

            // C1 fix: ResetWaterfall is called from the UI thread (StartLive / StartSweep).
            // Dispatcher.Invoke on the UI thread deadlocks — run the plot setup directly.
            void SetupPlot()
            {
                SecondaryPlot.Plot.Clear();
                _wfHeatmap = null;

                _wfHeatmap = SecondaryPlot.Plot.Add.Heatmap(newMatrix);
                _wfHeatmap.Colormap    = new Turbo();
                _wfHeatmap.Position    = new ScottPlot.CoordinateRect(
                                                freqMin, freqMax, 0, WaterfallRows);
                double top    = _refLevelDb + _calOffsetDb;
                double bottom = top - _dbTotalRange;
                _wfHeatmap.ManualRange = new ScottPlot.Range(bottom, top);

                SecondaryPlot.Plot.Axes.SetLimitsX(freqMin, freqMax);
                SecondaryPlot.Plot.Axes.SetLimitsY(0, WaterfallRows);
                SecondaryPlot.Refresh();
            }

            if (Dispatcher.CheckAccess())
                SetupPlot();           // already on UI thread — run directly, no Invoke
            else
                Dispatcher.Invoke(SetupPlot);  // called from background thread
        }

        private void PushWaterfallRow(double[] row)
        {
            // H2 fix: hold _wfLock while reading _wfBins and _wfMatrix so we never mix
            // a new _wfMatrix with an old _wfBins (or vice versa) from ResetWaterfall.
            lock (_wfLock)
            {
                int bins = Math.Min(row.Length, _wfBins);

                for (int r = WaterfallRows - 1; r > 0; r--)
                    Buffer.BlockCopy(_wfMatrix, (r - 1) * _wfBins * sizeof(double),
                                     _wfMatrix, r       * _wfBins * sizeof(double),
                                     _wfBins * sizeof(double));

                for (int b = 0; b < bins; b++)
                    _wfMatrix[0, b] = row[b];
            }
        }

        private void UpdateIqBufferFromRaw(short[] raw)
        {
            int n = Math.Min(_fftSize, raw.Length / 2);
            if (_iqI.Length != n) { _iqI = new double[n]; _iqQ = new double[n]; }
            for (int i = 0; i < n; i++)
            {
                _iqI[i] = raw[i * 2]     / 32768.0;
                _iqQ[i] = raw[i * 2 + 1] / 32768.0;
            }
        }

        // ── DSP ──────────────────────────────────────────────────────────────
        private void ComputeSpectrumAndPsd(short[] iq, out double[] spec, out double[] psd)
        {
            int n = _fftSize;

            // H3 fix: guard against IIO delivering fewer samples than expected.
            if (iq.Length < n * 2)
                throw new InvalidOperationException(
                    $"Sample buffer too small: got {iq.Length / 2} samples, need {n}.");

            _fftProvider.Compute(iq, n, _sampleRateHz, _calOffsetDb, out spec, out psd);
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

        // ── Status bar + right panel ─────────────────────────────────────────
        private void UpdateStatusBar()
        {
            string mode = GetMode();
            int avgDepth = VbwToAvgDepth(_rbwHz, _vbwHz);

            if (mode == "live")
            {
                TxtStatus.Text = $"Live — {_centerFreqHz / 1e6:F3} MHz  |  Fs = {FormatHz(_sampleRateHz)}  |  FFT = {_fftSize}  |  Avg = {avgDepth}";
                TxtSaInfo.Text = $"RBW {FormatHz(_rbwHz)}  •  VBW {FormatHz(_vbwHz)}  •  Span {FormatHz(_spanHz)}";
            }
            else
            {
                TxtSaInfo.Text = $"RBW {FormatHz(_rbwHz)}  •  VBW {FormatHz(_vbwHz)}  •  Step {FormatHz((long)(_sampleRateHz * SweepUseFraction))}";
            }

            UpdateRightPanel();
        }

        private void UpdateRightPanel()
        {
            if (!_plotsReady) return;

            string mode = GetMode();
            int avgDepth = VbwToAvgDepth(_rbwHz, _vbwHz);
            string rxAnt = (CmbRxAntenna.SelectedItem as ComboBoxItem)?.Tag as string ?? "—";

            // Branding bar readouts
            if (TxtReadoutCf   != null) TxtReadoutCf.Text   = _running ? $"{_centerFreqHz / 1e6:F3} MHz" : "—";
            if (TxtReadoutSpan != null) TxtReadoutSpan.Text  = _running ? FormatHz(_spanHz) : "—";
            if (TxtRbwActual   != null) TxtRbwActual.Text    = _running ? FormatHz(_rbwHz) : "—";
            if (TxtVbwActual   != null) TxtVbwActual.Text    = _running ? FormatHz(_vbwHz) : "—";
            if (TxtReadoutAvg  != null) TxtReadoutAvg.Text   = _running ? avgDepth.ToString() : "—";

            // Right panel — Hardware
            if (TxtInfoMode != null) TxtInfoMode.Text = mode == "live" ? "Live" : mode == "sweep" ? "Span Sweep" : "Full Sweep";
            if (TxtInfoFs   != null) TxtInfoFs.Text   = _running ? FormatHz(_sampleRateHz) : "—";
            if (TxtInfoFft  != null) TxtInfoFft.Text  = _running ? _fftSize.ToString() : "—";
            if (TxtInfoGain != null) TxtInfoGain.Text = $"{(int)SldGain.Value} dB";
            if (TxtInfoAnt  != null) TxtInfoAnt.Text  = rxAnt == "A_BALANCED" ? "Antenna 1" : rxAnt == "B_BALANCED" ? "Antenna 2" : rxAnt;

            // Right panel — Frequency
            if (TxtInfoCf    != null) TxtInfoCf.Text    = _running ? $"{_centerFreqHz / 1e6:F3} MHz" : "—";
            if (TxtInfoSpan  != null) TxtInfoSpan.Text  = _running ? FormatHz(_spanHz) : "—";
            if (TxtInfoStart != null) TxtInfoStart.Text = _running && _freqAxis.Length > 0 ? $"{_freqAxis[0]:F3} MHz" : "—";
            if (TxtInfoStop  != null) TxtInfoStop.Text  = _running && _freqAxis.Length > 0 ? $"{_freqAxis[^1]:F3} MHz" : "—";

            // Right panel — DSP
            if (TxtInfoRbw != null) TxtInfoRbw.Text = _running ? FormatHz(_rbwHz) : "—";
            if (TxtInfoVbw != null) TxtInfoVbw.Text = _running ? FormatHz(_vbwHz) : "—";
            if (TxtInfoAvg != null) TxtInfoAvg.Text = _running ? avgDepth.ToString() : "—";

            // Right panel — Display
            if (TxtInfoRef   != null) TxtInfoRef.Text   = $"{_refLevelDb:F0} dB";
            if (TxtInfoDbDiv != null) TxtInfoDbDiv.Text = $"{_dbTotalRange / 10} dB";
            if (TxtInfoCal   != null) TxtInfoCal.Text   = $"{_calOffsetDb:F1} dB";
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

        private bool TryParseFreqMhz(string text, out double mhz)
        {
            mhz = 0;
            return double.TryParse(text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out mhz) && mhz >= FreqMinMhz && mhz <= _freqMaxMhz;
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
