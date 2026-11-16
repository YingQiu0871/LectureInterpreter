using System.Diagnostics;
using System.Text;

namespace LiveCaptionsTranslator.utils
{
    // Audio capture of CloudAsr on Linux and macOS. Both start a helper process that writes raw 16 kHz mono
    // signed 16-bit little-endian PCM to its standard output:
    //   Linux: `parec` (PulseAudio, also served by PipeWire through pipewire-pulse); the computer's output is the
    //          monitor of the default sink.
    //   macOS: `lct-audio` (desktop/macos/lct-audio.swift) next to the program: ScreenCaptureKit for the computer's
    //          output, AVAudioEngine for the microphone.
    public static partial class CloudAsr
    {
        private const int PCM_RATE = 16000;

        // Linux only: the PulseAudio source used for the microphone instead of the default one (see AudioDevices).
        public static string? MicrophoneSource { get; set; }

        // A helper that ends by itself after running at least this long is started again (the sound server was
        // restarted, the device went away); one that ends at once has a problem a restart does not solve. Nor
        // does it go on for ever: after MAX_RESTARTS runs that were all short it is given up. A helper that ran
        // for STABLE_RUN was working, and what ends it then is a new interruption.
        private static readonly TimeSpan MIN_RUN = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan STABLE_RUN = TimeSpan.FromMinutes(1);
        private const int MAX_RESTARTS = 5;

        private static readonly object captureLock = new();
        private static Process? helper;
        private static bool capturing;
        private static bool captureMicrophone;
        private static int restarts;
        // Counts the classes: what the helper of an earlier one still has to say or to do is dropped.
        private static int captureRun;

        private static void BeginCapture(bool useMicrophone)
        {
            int run;
            lock (captureLock)
            {
                EndCapture();
                capturing = true;
                captureMicrophone = useMicrophone;
                restarts = 0;
                run = ++captureRun;
            }
            StartHelper(run);
        }

        // Starts the helper of the class `run`. Not under the lock as a whole: finding out what to start may ask
        // another program (`pactl`), and "停止" does not wait for that.
        private static void StartHelper(int run)
        {
            bool useMicrophone;
            lock (captureLock)
            {
                if (!capturing || run != captureRun)
                    return;
                useMicrophone = captureMicrophone;
            }
            var info = OperatingSystem.IsMacOS() ? MacHelper(run, useMicrophone) : LinuxHelper(run, useMicrophone);
            if (info == null)
                return;

            string? problem = null;
            lock (captureLock)
            {
                // Stopped, or stopped and started again, in the meantime.
                if (!capturing || run != captureRun || helper != null)
                    return;
                try
                {
                    var process = Process.Start(info);
                    if (process == null)
                    {
                        problem = "无法启动音频采集程序。";
                    }
                    else
                    {
                        helper = process;
                        var errors = new StringBuilder();
                        process.ErrorDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                                lock (errors)
                                    errors.AppendLine(e.Data);
                        };
                        process.BeginErrorReadLine();
                        _ = Task.Run(() => ReadLoop(process, errors, run));
                    }
                }
                catch (Exception ex)
                {
                    problem = $"无法启动音频采集：{ex.Message}";
                }
            }
            if (problem != null)
                Report(run, problem);
        }

        private static void EndCapture()
        {
            lock (captureLock)
            {
                capturing = false;
                Exec.Kill(helper);
                helper = null;
            }
        }

        private static ProcessStartInfo? LinuxHelper(int run, bool useMicrophone)
        {
            if (!Exec.Exists("parec"))
            {
                Report(run, "请先安装 pulseaudio-utils（parec），Linux 上靠它采集声音。");
                return null;
            }
            string source = "@DEFAULT_MONITOR@";
            if (useMicrophone)
            {
                source = "@DEFAULT_SOURCE@";
                string? chosen = MicrophoneSource;
                // A chosen microphone that is not plugged in: `parec` would end at once ("No such entity") and the
                // class would not start. The default one is used, as the settings page shows it.
                if (chosen != null && PulseSourceExists(chosen))
                    source = chosen;
                else if (chosen != null && IsFirstStart(run))
                    SnackbarHost.Show("选的麦克风现在没有连接。", "这次改用系统默认麦克风。", SnackbarType.Warning,
                        timeout: 5, closeButton: true);
            }
            return Exec.Create("parec", new[]
            {
                "--raw", "--format=s16le", $"--rate={PCM_RATE}", "--channels=1", "--latency-msec=100", "-d", source,
            });
        }

        // Whether the helper is about to be started for the first time in the class `run`, and that class is still
        // on: what is said once per class is not said again with every restart, nor (asking `pactl` takes a
        // moment) after "停止".
        private static bool IsFirstStart(int run)
        {
            lock (captureLock)
                return capturing && run == captureRun && restarts == 0;
        }

        // Whether `pactl` lists the source. True as well when it cannot tell: the source is then tried as chosen.
        private static bool PulseSourceExists(string name)
        {
            string? text = Exec.Run("pactl", new[] { "list", "short", "sources" }, 3000).GetAwaiter().GetResult();
            if (text == null)
                return true;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Any(fields => fields.Length > 1 && fields[1] == name);
        }

        private static ProcessStartInfo? MacHelper(int run, bool useMicrophone)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "lct-audio");
            if (!File.Exists(path))
            {
                Report(run, "找不到音频采集组件 lct-audio，请重新安装本程序。");
                return null;
            }
            return Exec.Create(path, new[] { useMicrophone ? "--mic" : "--system" });
        }

        // Reads the PCM until the helper ends. If that happens while capturing, starts it again or tells why.
        private static void ReadLoop(Process process, StringBuilder errors, int run)
        {
            var stream = process.StandardOutput.BaseStream;
            var buffer = new byte[PCM_RATE * 2 / 10];
            int carry = 0;
            var started = Stopwatch.StartNew();
            bool delivering = false;
            try
            {
                while (true)
                {
                    int read = stream.Read(buffer, carry, buffer.Length - carry);
                    if (read <= 0)
                        break;
                    if (!delivering)
                    {
                        // Sound arrives (again): a problem reported before is over.
                        delivering = true;
                        lock (captureLock)
                        {
                            if (ReferenceEquals(helper, process))
                                ReportCaptureProblem(string.Empty);
                        }
                    }
                    int total = carry + read;
                    int usable = total - (total % 2);
                    if (usable > 0)
                        OnAudio(buffer, usable, PCM_RATE, 1, 16, false);
                    // A read may end in the middle of a sample.
                    carry = total - usable;
                    if (carry > 0)
                        buffer[0] = buffer[usable];
                }
            }
            catch (Exception)
            {
                // The helper ended or was killed; the code below reports it if that was not on purpose.
            }

            lock (captureLock)
            {
                if (!ReferenceEquals(helper, process))
                    return;    // Stopped on purpose.
            }
            // The error text arrives asynchronously: wait for the helper to end and the pipes to drain.
            try
            {
                process.WaitForExit();
            }
            catch (Exception)
            {
                // The helper is already gone; its error text is read below.
            }
            string message;
            lock (errors)
                message = errors.ToString().Trim();
            if (message.Length == 0)
                message = "音频采集意外结束。";

            bool restart;
            lock (captureLock)
            {
                if (!ReferenceEquals(helper, process))
                    return;    // Stopped on purpose while waiting.
                helper = null;
                if (started.Elapsed >= STABLE_RUN)
                    restarts = 0;
                restart = started.Elapsed >= MIN_RUN && restarts < MAX_RESTARTS;
                if (restart)
                {
                    restarts++;
                    // Said under the lock, like everything about the capture: once the class is stopped
                    // nothing is reported any more.
                    ReportCaptureProblem($"音频采集中断了（{message.TrimEnd('。', '.', ' ')}），正在重新开始……");
                }
            }
            if (!restart)
            {
                Report(run, message);
                return;
            }

            Thread.Sleep(1000);
            StartHelper(run);
        }

        // Shows the problem above the current sentence (until sound arrives again, or the class stops) and as a
        // toast. Not when the class it is about was stopped in the meantime.
        private static void Report(int run, string message)
        {
            lock (captureLock)
            {
                if (!capturing || run != captureRun)
                    return;
                ReportCaptureProblem(message);
            }
            SnackbarHost.Show("[ERROR] 无法采集声音。", message, SnackbarType.Error, timeout: 8, closeButton: true);
        }
    }
}
