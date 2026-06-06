using System;
using System.Runtime.InteropServices;

namespace PlutoSpectrum
{
    internal sealed class PlutoSdr : IDisposable
    {
        public const string DefaultIp = "192.168.2.1";

        // AD9364 (Pluto+) hardware LO limits
        // Extended Pluto+ firmware supports up to 7 GHz via register hacking; standard is ~6 GHz.
        // We allow up to 7 GHz and clamp at 6 999 999 999 so the firmware never sees exactly 7 GHz.
        public const long LoMinHz = 70_000_000L;      // 70 MHz
        public const long LoMaxHz = 6_999_999_999L;   // just under 7 GHz

        // ── libiio P/Invoke ──────────────────────────────────────────────────
        private const string Lib = "libiio";

        [DllImport(Lib)] static extern IntPtr iio_create_network_context(string host);
        [DllImport(Lib)] static extern void   iio_context_destroy(IntPtr ctx);
        [DllImport(Lib)] static extern IntPtr iio_context_find_device(IntPtr ctx, string name);
        [DllImport(Lib)] static extern IntPtr iio_device_find_channel(IntPtr dev, string name, [MarshalAs(UnmanagedType.I1)] bool output);
        [DllImport(Lib)] static extern int    iio_channel_attr_write_longlong(IntPtr ch, string attr, long val);
        [DllImport(Lib)] static extern int    iio_channel_attr_write_double(IntPtr ch, string attr, double val);
        [DllImport(Lib)] static extern int    iio_channel_attr_write(IntPtr ch, string attr, string val);
        [DllImport(Lib)] static extern int    iio_channel_attr_read(IntPtr ch, string attr, System.Text.StringBuilder dst, nuint len);
        [DllImport(Lib)] static extern uint   iio_channel_get_attrs_count(IntPtr ch);
        [DllImport(Lib)] static extern IntPtr iio_channel_get_attr(IntPtr ch, uint index);

        // Integer attributes (rf_bandwidth, sampling_frequency, frequency): "2000000"
        private static int WriteInt(IntPtr ch, string attr, long val)
            => iio_channel_attr_write(ch, attr, val.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Float attributes (hardwaregain): "40.000000" — always dot separator
        private static int WriteDouble(IntPtr ch, string attr, double val)
            => iio_channel_attr_write(ch, attr,
                   val.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        [DllImport(Lib)] static extern void   iio_channel_enable(IntPtr ch);
        [DllImport(Lib)] static extern IntPtr iio_device_create_buffer(IntPtr dev, nuint samples, [MarshalAs(UnmanagedType.I1)] bool cyclic);
        [DllImport(Lib)] static extern void   iio_buffer_destroy(IntPtr buf);
        [DllImport(Lib)] static extern int    iio_buffer_refill(IntPtr buf);
        [DllImport(Lib)] static extern IntPtr iio_buffer_first(IntPtr buf, IntPtr ch);
        [DllImport(Lib)] static extern IntPtr iio_buffer_end(IntPtr buf);
        [DllImport(Lib)] static extern long   iio_buffer_step(IntPtr buf);
        // ────────────────────────────────────────────────────────────────────

        private IntPtr _ctx;
        private IntPtr _rxDev;
        private IntPtr _rxI;
        private IntPtr _rxQ;
        private IntPtr _buf;

        // Cached PHY channel handles
        private IntPtr _phyLo;           // altvoltage0 output
        private IntPtr _phyRx;           // voltage0 input
        private string _loFreqAttr = "frequency"; // actual attr name, probed at Open()

        private int _fftSize;

        public bool IsOpen { get; private set; }

        // Populated after Open() — the exact strings the firmware accepts for rf_port_select
        public string[] RxPortsAvailable { get; private set; } = [];

        public void Open(long centerFreqHz, long sampleRateHz, long rfBwHz, double gainDb,
                         int fftSize, string host = DefaultIp, string rxAntenna = "A_BALANCED")
        {
            _fftSize = fftSize;

            _ctx = iio_create_network_context(host);
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"Cannot connect to PlutoSDR at {host} — check USB cable and RNDIS adapter.");

            // Streaming device
            _rxDev = iio_context_find_device(_ctx, "cf-ad9361-lpc");
            if (_rxDev == IntPtr.Zero)
                throw new InvalidOperationException("Streaming device 'cf-ad9361-lpc' not found.");

            // PHY device
            var phy = iio_context_find_device(_ctx, "ad9361-phy");
            if (phy == IntPtr.Zero)
                throw new InvalidOperationException("PHY device 'ad9361-phy' not found.");

            // RX voltage channel (gain / bandwidth / sample rate)
            _phyRx = iio_device_find_channel(phy, "voltage0", false);
            if (_phyRx == IntPtr.Zero)
                throw new InvalidOperationException("PHY RX channel 'voltage0' (input) not found.");

            Check(WriteInt   (_phyRx, "sampling_frequency", sampleRateHz), "sampling_frequency", sampleRateHz);
            Check(WriteInt   (_phyRx, "rf_bandwidth",       rfBwHz),       "rf_bandwidth",       rfBwHz);
            Check(iio_channel_attr_write(_phyRx, "gain_control_mode", "manual"),              "gain_control_mode");
            Check(WriteDouble(_phyRx, "hardwaregain",        gainDb),       "hardwaregain",       (long)gainDb);
            RxPortsAvailable = ReadAvailable(_phyRx, "rf_port_select_available");
            if (RxPortsAvailable.Length > 0 && Array.IndexOf(RxPortsAvailable, rxAntenna) < 0)
                throw new InvalidOperationException(
                    $"RX port '{rxAntenna}' not valid. Available: [{string.Join(", ", RxPortsAvailable)}]");
            int portRet = iio_channel_attr_write(_phyRx, "rf_port_select", rxAntenna);
            if (portRet < 0)
            {
                string avail = RxPortsAvailable.Length > 0
                    ? string.Join(", ", RxPortsAvailable)
                    : "(could not read rf_port_select_available)";
                throw new InvalidOperationException(
                    $"rf_port_select write failed (errno {-portRet}: {ErrnoName(-portRet)}). " +
                    $"Tried: '{rxAntenna}'. Available: [{avail}]");
            }

            // LO channel — output channel named "altvoltage0"
            _phyLo = iio_device_find_channel(phy, "altvoltage0", true);
            if (_phyLo == IntPtr.Zero)
                throw new InvalidOperationException(
                    "LO channel 'altvoltage0' (output) not found on ad9361-phy. " +
                    "Check firmware version — expected ad9361-phy with altvoltage0.");

            // Probe actual LO frequency attribute name (differs between firmware versions)
            _loFreqAttr = ProbeLoAttr(_phyLo);

            // Wait for PHY to settle after sample rate change before touching the LO PLL
            System.Threading.Thread.Sleep(50);

            long clampedFreq = Math.Clamp(centerFreqHz, LoMinHz, LoMaxHz);
            Check(WriteInt(_phyLo, _loFreqAttr, clampedFreq), "LO frequency", clampedFreq);

            // Enable I/Q streaming channels
            _rxI = iio_device_find_channel(_rxDev, "voltage0", false);
            _rxQ = iio_device_find_channel(_rxDev, "voltage1", false);
            if (_rxI == IntPtr.Zero || _rxQ == IntPtr.Zero)
                throw new InvalidOperationException("Streaming I/Q channels not found on cf-ad9361-lpc.");

            iio_channel_enable(_rxI);
            iio_channel_enable(_rxQ);

            _buf = iio_device_create_buffer(_rxDev, (nuint)fftSize, false);
            if (_buf == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create IIO sample buffer.");

            IsOpen = true;
        }

        public short[] ReadSamples()
        {
            if (!IsOpen) throw new InvalidOperationException("Device not open.");

            int ret = iio_buffer_refill(_buf);
            if (ret < 0) throw new InvalidOperationException($"iio_buffer_refill failed ({ret}).");

            IntPtr start = iio_buffer_first(_buf, _rxI);
            IntPtr end   = iio_buffer_end(_buf);
            long   step  = iio_buffer_step(_buf);

            int count   = (int)((end.ToInt64() - start.ToInt64()) / step);
            var samples = new short[count * 2];

            unsafe
            {
                byte* ptr = (byte*)start.ToPointer();
                for (int i = 0; i < count; i++)
                {
                    samples[i * 2]     = *(short*)(ptr + i * step);
                    samples[i * 2 + 1] = *(short*)(ptr + i * step + 2);
                }
            }

            return samples;
        }

        // Uses the cached _phyLo handle — safe and fast to call inside the sweep loop
        public void SetCenterFreq(long freqHz)
        {
            if (!IsOpen || _phyLo == IntPtr.Zero) return;
            long clamped = Math.Clamp(freqHz, LoMinHz, LoMaxHz);
            int ret = WriteInt(_phyLo, _loFreqAttr, clamped);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"LO set failed: attr='{_loFreqAttr}' requested={freqHz} clamped={clamped} errno={-ret} ({ErrnoName(-ret)})");
        }

        public void SetGain(double gainDb)
        {
            if (!IsOpen || _phyRx == IntPtr.Zero) return;
            WriteDouble(_phyRx, "hardwaregain", gainDb);
        }

        public void Dispose()
        {
            IsOpen = false;
            if (_buf != IntPtr.Zero) { iio_buffer_destroy(_buf); _buf = IntPtr.Zero; }
            if (_ctx != IntPtr.Zero) { iio_context_destroy(_ctx); _ctx = IntPtr.Zero; }
        }

        // Enumerate altvoltage0 attrs and return the right frequency attr name.
        // Stock firmware → "frequency"; some extended builds → "RX_LO" or "TX_LO".
        private string ProbeLoAttr(IntPtr ch)
        {
            uint count = iio_channel_get_attrs_count(ch);
            var  found = new System.Collections.Generic.List<string>();
            var  buf   = new System.Text.StringBuilder(64);

            for (uint i = 0; i < count; i++)
            {
                IntPtr namePtr = iio_channel_get_attr(ch, i);
                if (namePtr == IntPtr.Zero) continue;
                string name = Marshal.PtrToStringAnsi(namePtr) ?? "";
                found.Add(name);
            }

            // Prefer exact "frequency", fall back to anything containing "freq"
            foreach (string a in found)
                if (a == "frequency") return "frequency";
            foreach (string a in found)
                if (a.IndexOf("freq", StringComparison.OrdinalIgnoreCase) >= 0) return a;

            // Nothing matched — throw with the full list so we know what's there
            throw new InvalidOperationException(
                $"Cannot find frequency attribute on altvoltage0. " +
                $"Available attrs: [{string.Join(", ", found)}]");
        }

        // Read a space-separated "_available" attribute and return the individual tokens.
        private static string[] ReadAvailable(IntPtr ch, string attr)
        {
            var sb  = new System.Text.StringBuilder(256);
            int ret = iio_channel_attr_read(ch, attr, sb, (nuint)sb.Capacity);
            if (ret < 0) return [];
            return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static void Check(int result, string label, long value = long.MinValue)
        {
            if (result < 0)
            {
                string valInfo = value != long.MinValue ? $" (value={value})" : "";
                throw new InvalidOperationException(
                    $"IIO error on '{label}'{valInfo}: {result}  " +
                    $"(errno {-result}: {ErrnoName(-result)})");
            }
        }

        private static string ErrnoName(int e) => e switch
        {
            1  => "EPERM",
            2  => "ENOENT",
            5  => "EIO",
            11 => "EAGAIN",
            12 => "ENOMEM",
            16 => "EBUSY",
            19 => "ENODEV",
            22 => "EINVAL — value out of range or device not ready",
            28 => "ENOSPC",
            110 => "ETIMEDOUT",
            _  => "unknown"
        };
    }
}
