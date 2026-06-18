using System;
using System.Runtime.InteropServices;

namespace PlutoSpectrum
{
    internal sealed class PlutoSdr : IDisposable
    {
        public const string DefaultIp = "192.168.2.1";

        // AD9364 (Pluto+) hardware LO limits.
        // Standard AD9364 firmware accepts 70 MHz – 6 GHz.
        // Extended Pluto+ firmware can go to ~6.999 GHz but EINVAL is returned if the firmware
        // was not patched. Use the 6 GHz safe default; pass a custom loMaxHz to Open() for
        // extended firmware builds.
        public const long LoMinHz        = 70_000_000L;       // 70 MHz — hardware minimum
        public const long LoMaxHz        = 6_000_000_000L;    // 6 GHz  — safe default (standard firmware)
        public const long LoMaxHzExtended = 6_999_999_000L;   // ~7 GHz — extended firmware only

        // Actual limits in use for this session (set by Open, readable by UI).
        public long ActualLoMinHz { get; private set; } = LoMinHz;
        public long ActualLoMaxHz { get; private set; } = LoMaxHz;

        // ── libiio P/Invoke ──────────────────────────────────────────────────
        private const string Lib = "libiio";

        [DllImport(Lib)] static extern IntPtr iio_create_network_context(string host);
        [DllImport(Lib)] static extern void   iio_context_destroy(IntPtr ctx);
        [DllImport(Lib)] static extern IntPtr iio_context_find_device(IntPtr ctx, string name);
        [DllImport(Lib)] static extern IntPtr iio_device_find_channel(IntPtr dev, string name, [MarshalAs(UnmanagedType.I1)] bool output);
        [DllImport(Lib)] static extern int    iio_channel_attr_write_longlong(IntPtr ch, string attr, long val);
        [DllImport(Lib)] static extern int    iio_channel_attr_write_double(IntPtr ch, string attr, double val);
        [DllImport(Lib)] static extern int    iio_channel_attr_write(IntPtr ch, string attr, string val);
        // M5 fix: use byte[] (not StringBuilder) so the marshaller passes a plain char* buffer
        // that libiio writes UTF-8 bytes into, rather than LPWSTR (UTF-16) which misinterprets them.
        [DllImport(Lib)] static extern int    iio_channel_attr_read(IntPtr ch, string attr, byte[] dst, nuint len);
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
        // M4 fix: iio_buffer_step returns ptrdiff_t/ssize_t — use nint, not long.
        [DllImport(Lib)] static extern nint   iio_buffer_step(IntPtr buf);
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
                         int fftSize, string host = DefaultIp, string rxAntenna = "A_BALANCED",
                         long loMaxHz = LoMaxHz)
        {
            _fftSize = fftSize;
            ActualLoMinHz = LoMinHz;
            ActualLoMaxHz = Math.Clamp(loMaxHz, LoMinHz, LoMaxHzExtended);

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
            // W7 note: small settle between sampling_frequency and rf_bandwidth to avoid EINVAL
            // on some firmware revisions where the ADC PLL recalibration races the next write.
            System.Threading.Thread.Sleep(5);
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

            long clampedFreq = Math.Clamp(centerFreqHz, ActualLoMinHz, ActualLoMaxHz);
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
            nint   step  = iio_buffer_step(_buf);   // M4 fix: nint matches ptrdiff_t

            int count = (int)((end.ToInt64() - start.ToInt64()) / step);

            // H3 fix: guard against IIO delivering a different count than _fftSize
            if (count < _fftSize)
                throw new InvalidOperationException(
                    $"IIO buffer delivered {count} samples; expected {_fftSize}. " +
                    "Buffer underrun or driver size mismatch.");

            var samples = new short[_fftSize * 2];

            unsafe
            {
                byte* ptr = (byte*)start.ToPointer();
                for (int i = 0; i < _fftSize; i++)
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
            long clamped = Math.Clamp(freqHz, ActualLoMinHz, ActualLoMaxHz);
            int result = WriteInt(_phyLo, _loFreqAttr, clamped);
            if (result == -22 && clamped > LoMaxHz)
            {
                // Firmware rejected a frequency above 6 GHz — standard firmware only.
                // Lower the session limit and retry within the safe range.
                ActualLoMaxHz = LoMaxHz;
                clamped = Math.Clamp(freqHz, ActualLoMinHz, ActualLoMaxHz);
                result = WriteInt(_phyLo, _loFreqAttr, clamped);
            }
            if (result < 0)
                throw new InvalidOperationException(
                    $"LO set failed: attr='{_loFreqAttr}' requested={freqHz} clamped={clamped} errno={-result} ({ErrnoName(-result)})");
        }

        public void SetGain(double gainDb)
        {
            if (!IsOpen || _phyRx == IntPtr.Zero) return;
            // M2 fix: check return value — a failure here means the hardware gain did not change.
            int result = WriteDouble(_phyRx, "hardwaregain", gainDb);
            if (result < 0)
                throw new InvalidOperationException(
                    $"hardwaregain write failed (errno {-result}: {ErrnoName(-result)}). " +
                    "Ensure gain_control_mode is set to 'manual'.");
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

        // M3+M5 fix: use byte[] so P/Invoke passes a plain char* (not wchar_t*).
        // libiio writes ASCII/UTF-8; decode with Latin-1 to preserve byte values exactly.
        // Buffer is 1024 bytes to safely hold extended rf_port_select_available strings.
        private static string[] ReadAvailable(IntPtr ch, string attr)
        {
            var buf = new byte[1024];
            int ret = iio_channel_attr_read(ch, attr, buf, (nuint)buf.Length);
            if (ret < 0) return [];
            // Decode as UTF-8; trim at null terminator if present.
            string raw = System.Text.Encoding.UTF8.GetString(buf, 0, ret);
            return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
