using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using xISOBurn.Core;
using xISOBurn.Scsi;
using xISOBurn.Spike;
using xISOBurn.Util;

namespace xISOBurn
{
    /// <summary>One log line on screen (its own object so ScrollIntoView works with repeated lines).</summary>
    public sealed class LogLine
    {
        public LogLine(string text) { Text = text; }
        public string Text { get; private set; }
    }

    public sealed partial class MainPage : Page
    {
        private const int MaxUiLines = 3000;
        private readonly ObservableCollection<LogLine> _lines = new ObservableCollection<LogLine>();
        private readonly string _folder = ApplicationData.Current.LocalFolder.Path;
        private readonly Logger _log;

        // The drive is always opened and used on thread-pool (MTA) threads, as XBSX2 does; the UI only awaits.
        private OpticalDrive _drive;
        private DiscProbe _probe;
        private CancellationTokenSource _cts;
        private bool _busy;

        public MainPage()
        {
            InitializeComponent();
            LogList.ItemsSource = _lines;
            _log = new Logger(_folder, "xisoburn", AddLine);
            _log.Info("xISOBurn started. Data folder: " + _folder);

            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            _log.Info(msg);
            ConfigText.Text = cfg.Describe();
            StatusText.Text = "Insert the disc and choose \"Detect disc\". Files and logs: " + _folder;

            Loaded += (s, e) => DetectButton.Focus(FocusState.Programmatic);
            // On Xbox, the B button raises BackRequested. Unhandled, the system may close the app mid-copy.
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = true;
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusText.Text = "Cancelling... (finishing the current block)";
            }
        }

        private void AddLine(string line)
        {
            if (!Dispatcher.HasThreadAccess)
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => AddLine(line));
                return;
            }
            if (_lines.Count >= MaxUiLines) _lines.RemoveAt(0);
            var item = new LogLine(line);
            _lines.Add(item);
            LogList.ScrollIntoView(item);
        }

        private void SetBusy(bool busy, bool copying)
        {
            _busy = busy;
            DetectButton.IsEnabled = !busy;
            CopyButton.IsEnabled = !busy;
            ScanButton.IsEnabled = !busy;
            SpikeButton.IsEnabled = !busy;
            CancelButton.IsEnabled = copying;
            if (copying) CancelButton.Focus(FocusState.Programmatic);
        }

        private async Task<bool> EnsureDriveAsync()
        {
            if (_drive != null) return true;
            try
            {
                _drive = await Task.Run(() => OpticalDrive.FindAndOpenAsync(CopyConfig.MaxSectorsPerRead * OpticalDrive.SectorSize));
                _log.Info("Drive opened: " + _drive.DeviceId);
                return true;
            }
            catch (Exception ex)
            {
                _log.Info("Could not open the drive: 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                StatusText.Text = "Drive not found or access denied (see log).";
                return false;
            }
        }

        private void CloseDrive()
        {
            if (_drive != null)
            {
                _drive.Dispose();
                _drive = null;
            }
        }

        private async Task<bool> DetectAsync(uint timeout)
        {
            if (!await EnsureDriveAsync()) return false;
            var drive = _drive;
            _probe = await Task.Run(() => DiscProbe.RunAsync(drive, timeout, _log));
            DiscText.Text = string.Join("\n", _probe.Describe());
            if (!_probe.Ready && _probe.NotReadyReason.Contains("IOCTL"))
                CloseDrive(); // the handle may have gone stale; reopen next time
            return _probe.Ready && _probe.TotalSectors > 0;
        }

        private async void DetectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, false);
            try
            {
                bool ok = await DetectAsync(20);
                StatusText.Text = ok ? "Disc detected. Choose \"Rescue / resume\"." : "No disc detected (see log).";
            }
            catch (Exception ex)
            {
                _log.Info("ERROR while detecting: " + ex);
                StatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                SetBusy(false, false);
                DetectButton.Focus(FocusState.Programmatic);
            }
        }

        private async void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, false);
            var display = new DisplayRequest();
            bool displayActive = false;
            try
            {
                // Reload the config on every copy, so edits made through the Device Portal take effect.
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                _log.Info(msg);
                ConfigText.Text = cfg.Describe();

                // Detect again: the disc may have been swapped since the last detection.
                if (!await DetectAsync((uint)cfg.TimeoutSeconds))
                {
                    StatusText.Text = "No disc ready to copy (see log).";
                    return;
                }

                // Keep the screen on: on Xbox, an app that leaves the screen or lets it dim gets suspended.
                display.RequestActive();
                displayActive = true;
                _cts = new CancellationTokenSource();
                SetBusy(true, true);
                StatusText.Text = "Rescuing... do not leave the app. B pauses (progress is kept; run again to resume).";
                Bar.Value = 0;

                var progress = new Progress<RescueProgress>(UpdateProgress);
                var job = new RescueJob(_drive, _probe, cfg, _log, _folder);
                var token = _cts.Token;
                var result = await Task.Run(() => job.RunAsync(progress, token));

                string counts = result.Ok.ToString("N0") + " ok, " + result.Bad.ToString("N0") + " bad, " +
                                result.Pending.ToString("N0") + " pending, " + result.Untried.ToString("N0") + " not tried";
                if (result.Finished)
                    StatusText.Text = "Finished: " + counts + "." + (result.Sha256 != null ? " SHA-256 " + result.Sha256 : "") +
                                      " Raise RetryPasses in config.json to keep trying the bad sectors.";
                else if (result.Cancelled)
                    StatusText.Text = "Paused: " + counts + ". Choose \"Rescue / resume\" to continue.";
                else if (result.Error != null)
                    StatusText.Text = "Stopped: " + result.Error + " (" + counts + ")";
                else
                    StatusText.Text = "Range done: " + counts + ".";
            }
            catch (Exception ex)
            {
                _log.Info("ERROR while copying: " + ex);
                StatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (displayActive) display.RequestRelease();
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                SetBusy(false, false);
                CopyButton.Focus(FocusState.Programmatic);
            }
        }

        private void UpdateProgress(RescueProgress p)
        {
            // The bar shows the whole disc (sectors read OK); the text shows the current phase.
            double okPct = p.Total > 0 ? p.Ok * 100.0 / p.Total : 0;
            double phasePct = p.PhaseTotal > 0 ? p.PhaseDone * 100.0 / p.PhaseTotal : 100;
            Bar.Value = okPct;
            ProgressText.Text =
                p.Phase + ": " + p.PhaseDone.ToString("N0") + " / " + p.PhaseTotal.ToString("N0") + " (" + phasePct.ToString("0.0") + "%) | LBA " + p.CurrentLba.ToString("N0") + "\n" +
                "Speed: " + (p.BytesPerSecond / 1048576.0).ToString("0.00") + " MiB/s | Elapsed: " + RescueJob.FormatTime(p.Elapsed) +
                " | Phase remaining: " + (p.Remaining.HasValue ? RescueJob.FormatTime(p.Remaining.Value) : "-") + "\n" +
                "Disc: " + okPct.ToString("0.000") + "% read | bad " + p.Bad.ToString("N0") + " | pending " + p.Pending.ToString("N0") +
                " | not tried " + p.Untried.ToString("N0");
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, false);
            var display = new DisplayRequest();
            bool displayActive = false;
            try
            {
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                if (!await DetectAsync((uint)cfg.TimeoutSeconds))
                {
                    StatusText.Text = "No disc ready to scan (see log).";
                    return;
                }
                display.RequestActive();
                displayActive = true;
                _cts = new CancellationTokenSource();
                SetBusy(true, true);
                StatusText.Text = "Scanning (reads ~90 spots once each, writes nothing)... B cancels.";
                var progress = new Progress<string>(s => ProgressText.Text = s);
                var scan = new DiscScan(_drive, _probe, _log, (uint)cfg.TimeoutSeconds);
                var token = _cts.Token;
                string summary = await Task.Run(() => scan.RunAsync(progress, token));
                StatusText.Text = summary;
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                _log.Info("ERROR while scanning: " + ex);
                StatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (displayActive) display.RequestRelease();
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                SetBusy(false, false);
                ScanButton.Focus(FocusState.Programmatic);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusText.Text = "Cancelling... (finishing the current block)";
            }
        }

        private async void SpikeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, false);
            CloseDrive(); // the spike opens its own handle
            _lines.Clear();
            StatusText.Text = "Running the feasibility test... do not leave the app.";
            var display = new DisplayRequest();
            display.RequestActive();
            Logger log = null;
            try
            {
                log = new Logger(_folder, "spike", AddLine);
                await new SpikeRunner(log, _folder).RunAsync();
                StatusText.Text = "Test finished. Log: " + log.FilePath;
            }
            catch (Exception ex)
            {
                AddLine("ERROR: " + ex);
                StatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                if (log != null) log.Dispose();
                display.RequestRelease();
                SetBusy(false, false);
                SpikeButton.Focus(FocusState.Programmatic);
            }
        }
    }
}
