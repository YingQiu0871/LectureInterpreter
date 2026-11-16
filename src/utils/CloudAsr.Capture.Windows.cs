#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveCaptionsTranslator.utils
{
    // Audio capture of CloudAsr on Windows: the computer's output (WASAPI loopback) or the default microphone.
    //
    // A capture is tied to one device, but which device is "the default" changes during a class: earphones are
    // plugged in or connected over Bluetooth, another microphone is chosen in the settings, a device is unplugged.
    // The old device then delivers silence (or stops) without any error, and the captions simply end. So the
    // default device is looked up every few seconds, and the capture moves to it when it has changed or stopped.
    public static partial class CloudAsr
    {
        private const int DEVICE_CHECK_MS = 2000;

        private static readonly object captureLock = new();
        private static readonly System.Threading.Timer deviceTimer =
            new(_ => CheckDevice(), null, Timeout.Infinite, Timeout.Infinite);
        private static WasapiCapture? capture;
        // The device the capture was last opened on (or tried to be), and the last one that worked in this class.
        private static string? captureDeviceId;
        private static string? workingDeviceId;
        private static bool captureMicrophone;
        private static bool capturing;
        // Set on the capture thread when the device stops delivering audio (unplugged, format changed, ...).
        private static volatile bool captureStopped;

        private static void BeginCapture(bool useMicrophone)
        {
            lock (captureLock)
            {
                CloseCapture();
                captureMicrophone = useMicrophone;
                captureDeviceId = null;
                workingDeviceId = null;
                capturing = true;
                TryOpenCapture();
                deviceTimer.Change(DEVICE_CHECK_MS, DEVICE_CHECK_MS);
            }
        }

        private static void EndCapture()
        {
            lock (captureLock)
            {
                capturing = false;
                deviceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                CloseCapture();
            }
        }

        // Follows the default device, and starts again after the device stopped. Runs on a timer thread.
        private static void CheckDevice()
        {
            // A round that is still opening a device is not overtaken by the next one.
            if (!Monitor.TryEnter(captureLock))
                return;
            try
            {
                if (!capturing)
                    return;
                if (capture != null && !captureStopped)
                {
                    // Capturing: only a different default device is a reason to start over. (A lookup that fails
                    // is not: the capture at hand works.)
                    if (!TryGetDefaultDeviceId(captureMicrophone, out string? current) || current == captureDeviceId)
                        return;
                }
                CloseCapture();
                TryOpenCapture();
            }
            catch (Exception ex)
            {
                // Runs on a timer thread, where an exception would end the program.
                ErrorLog.Write("CloudAsr.Capture", ex);
            }
            finally
            {
                Monitor.Exit(captureLock);
            }
        }

        private static (DataFlow Flow, Role Role) Endpoint(bool useMicrophone) =>
            // The same defaults NAudio itself uses for a capture without a device.
            useMicrophone ? (DataFlow.Capture, Role.Console) : (DataFlow.Render, Role.Multimedia);

        // The id of the default device (null: there is none). False when it could not be looked up.
        private static bool TryGetDefaultDeviceId(bool useMicrophone, out string? id)
        {
            id = null;
            try
            {
                var (flow, role) = Endpoint(useMicrophone);
                using var enumerator = new MMDeviceEnumerator();
                if (enumerator.HasDefaultAudioEndpoint(flow, role))
                {
                    // Asked every few seconds for a whole class: not left to the garbage collector.
                    using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                    id = device.ID;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Opens the default device, or says why that is not possible (the timer then tries again).
        private static void TryOpenCapture()
        {
            try
            {
                string? name = OpenCapture();
                if (name == null)
                {
                    ReportCaptureProblem(captureMicrophone
                        ? "没有找到可用的麦克风。接上或启用麦克风后会自动继续识别。"
                        : "没有找到播放设备（扬声器或耳机）。接上或启用后会自动继续识别。");
                    return;
                }
                ReportCaptureProblem(string.Empty);

                // Say so when the class goes on with another device than before.
                bool moved = workingDeviceId != null && workingDeviceId != captureDeviceId;
                workingDeviceId = captureDeviceId;
                if (!moved)
                    return;
                if (captureMicrophone)
                    SnackbarHost.Show($"麦克风已切换到：{name}", "如果这不是要用的麦克风，请到“设置”页的“⓪ 输入源”里重新选择。",
                        SnackbarType.Info, timeout: 6, closeButton: true);
                else
                    SnackbarHost.Show($"现在识别这个设备播放的声音：{name}", "Windows 的默认播放设备变了，识别跟着切换。",
                        SnackbarType.Info, timeout: 6, closeButton: true);
            }
            catch (Exception ex)
            {
                CloseCapture();
                ErrorLog.Write("CloudAsr.Capture", ex);
                ReportCaptureProblem((captureMicrophone ? "无法使用麦克风：" : "无法采集电脑播放的声音：") + ex.Message +
                                     "。会自动重试；如果一直不行，可能有别的程序独占了这个设备：请在 Windows 声音设置里，" +
                                     "到该设备“高级”页取消“允许应用程序独占控制该设备”。");
            }
        }

        // Returns the name of the device now captured, or null when there is no default device.
        private static string? OpenCapture()
        {
            var (flow, role) = Endpoint(captureMicrophone);
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(flow, role))
            {
                captureDeviceId = null;
                return null;
            }
            var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            // Remembered even if opening fails: the same broken device is then retried, not announced as new.
            captureDeviceId = device.ID;

            var opened = captureMicrophone ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
            try
            {
                var format = opened.WaveFormat;
                bool isFloat = format.BitsPerSample == 32 && format.Encoding != WaveFormatEncoding.Pcm;
                opened.DataAvailable += (s, e) =>
                    OnAudio(e.Buffer, e.BytesRecorded, format.SampleRate, format.Channels, format.BitsPerSample, isFloat);
                // Raised on the capture thread (or posted to the UI thread). It only leaves a note for the timer:
                // closing a capture waits for its thread, so nothing here may wait for the lock held meanwhile.
                opened.RecordingStopped += (s, e) =>
                {
                    if (ReferenceEquals(Volatile.Read(ref capture), opened))
                        captureStopped = true;
                };
                ResetSignalState();
                captureStopped = false;
                Volatile.Write(ref capture, opened);
                opened.StartRecording();
            }
            catch (Exception)
            {
                Volatile.Write(ref capture, null);
                try
                {
                    opened.Dispose();
                }
                catch (Exception)
                {
                    // Best effort: the device may be gone already.
                }
                throw;
            }
            return device.FriendlyName;
        }

        private static void CloseCapture()
        {
            var closing = capture;
            Volatile.Write(ref capture, null);
            if (closing == null)
                return;
            try
            {
                closing.StopRecording();
                closing.Dispose();
            }
            catch (Exception)
            {
                // Best effort: the capture device may already be stopped or unplugged.
            }
        }
    }
}
#endif
