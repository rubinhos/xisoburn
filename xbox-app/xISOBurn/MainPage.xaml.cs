using System;
using System.Collections.Generic;
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
        /// <summary>Bad + pending sectors of the inserted disc according to its .map (enables "Retry failed").</summary>
        private long _failedSectors;
        private string _rescueLine = "";

        // Web page served by the app (milestone 3b spike). The HTML fragment is rebuilt on the UI
        // thread once a second and read by the server threads (strings are immutable: no lock needed).
        private static readonly int[] WebPorts = { 8770, 8080, 50080 };
        private WebServer _web;
        private volatile string _webBody = "";
        private DispatcherTimer _webTimer;

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
            StatusText.Text = cfg.SendToPc && cfg.ReceiverHost.Length == 0
                ? "Start xisoburn-receiver on the PC, then open Settings and enter its address and token."
                : "Insert the disc and choose \"Detect disc\". Logs: " + _folder;

            Loaded += async (s, e) =>
            {
                DetectButton.Focus(FocusState.Programmatic);
                await StartWebAsync();
            };
            // On Xbox, the B button raises BackRequested. Unhandled, the system may close the app mid-copy.
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        private async Task StartWebAsync()
        {
            try
            {
                _web = new WebServer(_log, () => _webBody);
                await _web.StartAsync(WebPorts);
                var addrs = WebServer.LocalAddresses();
                if (_web.Ports.Count == 0 || addrs.Count == 0)
                {
                    SubtitleText.Text = "Web page unavailable (see log)";
                    if (addrs.Count == 0) _log.Info("Web server: no IPv4 address found on the console's network adapters.");
                    return;
                }
                var urls = new List<string>();
                foreach (var a in addrs)
                    foreach (var p in _web.Ports)
                        urls.Add("http://" + a + ":" + p + "/");
                SubtitleText.Text = "Open on the PC: " + urls[0];
                _log.Info("Web page (open in the PC browser): " + string.Join("  ", urls));
            }
            catch (Exception ex)
            {
                _log.Info("Web server failed to start: " + ex.Message);
            }

            _webTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _webTimer.Tick += (s, e) => UpdateWebBody();
            _webTimer.Start();
            UpdateWebBody();
        }

        private void UpdateWebBody()
        {
            Func<string, string> enc = System.Net.WebUtility.HtmlEncode;
            var sb = new System.Text.StringBuilder();
            sb.Append("<h2>Disc</h2><div class=\"box\">").Append(enc(DiscText.Text)).Append("</div>");
            sb.Append("<h2>Progress</h2><div class=\"box\">").Append(enc(ProgressText.Text)).Append("</div>");
            sb.Append("<h2>Status</h2><div class=\"box\">").Append(enc(StatusText.Text)).Append("</div>");
            sb.Append("<h2>Recent log</h2><pre>");
            int start = Math.Max(0, _lines.Count - 40);
            for (int i = start; i < _lines.Count; i++) sb.Append(enc(_lines[i].Text)).Append('\n');
            sb.Append("</pre>");
            _webBody = sb.ToString();
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
            RetryButton.IsEnabled = !busy && _failedSectors > 0;
            ScanButton.IsEnabled = !busy;
            SettingsButton.IsEnabled = !busy;
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
            if (!_probe.Ready && _probe.NotReadyReason.Contains("IOCTL"))
                CloseDrive(); // the handle may have gone stale; reopen next time
            await CheckPreviousRescueAsync();
            return _probe.Ready && _probe.TotalSectors > 0;
        }

        /// <summary>
        /// Looks for a .map of an earlier rescue of this disc, so the screen can say how far it got and
        /// "Retry failed" is only offered when there is something to retry.
        /// </summary>
        private async Task CheckPreviousRescueAsync()
        {
            _failedSectors = 0;
            _rescueLine = "";
            if (_probe != null && _probe.Ready && _probe.TotalSectors > 0)
            {
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                string path = System.IO.Path.Combine(_folder, RescueJob.BaseNameFor(cfg, _probe) + ".map");
                long total = _probe.TotalSectors;
                SectorStateMap map = null;
                try
                {
                    if (System.IO.File.Exists(path))
                        map = await Task.Run(() => { string reason; return SectorStateMap.Load(path, total, out reason); });
                }
                catch (Exception ex)
                {
                    _log.Info("Could not read the previous map: " + ex.Message);
                }
                if (map != null)
                    SetRescueState(map.Count(SectorState.Ok), map.Count(SectorState.Bad), map.Count(SectorState.Pending),
                                   map.Count(SectorState.Untried), total);
            }
            ShowDisc();
        }

        private void SetRescueState(long ok, long bad, long pending, long untried, long total)
        {
            _failedSectors = bad + pending;
            _rescueLine = "Previous rescue: " + (ok * 100.0 / total).ToString("0.000") + "% read" +
                          (_failedSectors > 0 ? ", " + _failedSectors.ToString("N0") + " failed sector(s)" : "") +
                          (untried > 0 ? ", " + untried.ToString("N0") + " not tried" : "") +
                          (untried == 0 && _failedSectors == 0 ? " (complete)" : "");
            _log.Info(_rescueLine);
            RetryButton.IsEnabled = !_busy && _failedSectors > 0;
        }

        private void ShowDisc()
        {
            if (_probe == null) return;
            string text = string.Join("\n", _probe.Describe());
            if (_rescueLine.Length > 0) text += "\n" + _rescueLine;
            DiscText.Text = text;
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
            await RunRescueAsync(false, CopyButton);
        }

        /// <summary>One more retry pass over the sectors that failed (in addition to the configured passes).</summary>
        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await RunRescueAsync(true, RetryButton);
        }

        private async Task RunRescueAsync(bool extraRetryPass, Button origin)
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
                if (cfg.WriteImage && cfg.SendToPc && cfg.ReceiverHost.Length == 0)
                {
                    StatusText.Text = "Open Settings and enter the PC address (or choose LocalState as the destination).";
                    return;
                }

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
                var job = new RescueJob(_drive, _probe, cfg, _log, _folder, extraRetryPass);
                var token = _cts.Token;
                var result = await Task.Run(() => job.RunAsync(progress, token));

                SetRescueState(result.Ok, result.Bad, result.Pending, result.Untried, _probe.TotalSectors);
                ShowDisc();

                string counts = result.Ok.ToString("N0") + " ok, " + result.Bad.ToString("N0") + " bad, " +
                                result.Pending.ToString("N0") + " pending, " + result.Untried.ToString("N0") + " not tried";
                if (extraRetryPass && !result.Cancelled && result.Error == null)
                    StatusText.Text = "Retry done: recovered " + result.RecoveredThisRun.ToString("N0") + " of " +
                                      result.RetriedThisRun.ToString("N0") + " sector(s); " + (result.Bad + result.Pending).ToString("N0") +
                                      " still failed." + (result.Sha256 != null ? " SHA-256 " + result.Sha256 : "");
                else if (result.Finished)
                    StatusText.Text = "Finished: " + counts + "." + (result.Sha256 != null ? " SHA-256 " + result.Sha256 : "") +
                                      (result.Bad > 0 ? " \"Retry failed\" tries the bad sectors once more." : "");
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
                origin.Focus(FocusState.Programmatic);
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

        // ------------------------------------------------------------ settings

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            SelectByTag(DestinationBox, cfg.Destination);
            HostBox.Text = cfg.ReceiverHost;
            PortBox.Text = cfg.ReceiverPort.ToString();
            TokenBox.Text = cfg.ReceiverToken;
            OutputNameBox.Text = cfg.OutputName;
            WriteImageBox.IsChecked = cfg.WriteImage;
            SelectByTag(SectorsPerReadBox, cfg.SectorsPerRead.ToString());
            TimeoutBox.Text = cfg.TimeoutSeconds.ToString();
            SkipBox.IsChecked = cfg.SkipOnErrors;
            RetryPassesBox.Text = cfg.RetryPasses.ToString();
            SelectByTag(RetrySpeedBox, cfg.RetryReadSpeedKBps.ToString());
            SelectByTag(DriveRetryBox, cfg.DriveReadRetryCount.ToString());
            StartBox.Text = cfg.StartSector.ToString();
            LimitBox.Text = cfg.SectorLimit.ToString();
            SettingsError.Text = "";
            TestText.Text = "";
            await SettingsDialog.ShowAsync();
            SettingsButton.Focus(FocusState.Programmatic);
        }

        private void SettingsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string error;
            var cfg = ReadSettings(out error);
            if (cfg == null)
            {
                SettingsError.Text = error;
                args.Cancel = true; // keep the dialog open so the value can be fixed
                return;
            }
            cfg.Save(_folder);
            string msg;
            cfg = CopyConfig.Load(_folder, out msg); // normalises the values exactly as a run will see them
            ConfigText.Text = cfg.Describe();
            _log.Info("Settings saved: " + cfg.Describe());
            StatusText.Text = "Settings saved. They apply from the next run; no restart needed.";
        }

        /// <summary>Builds a config from the dialog; null (with a message) if a field is invalid.</summary>
        private CopyConfig ReadSettings(out string error)
        {
            error = null;
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            cfg.Destination = TagOf(DestinationBox, "pc");
            cfg.ReceiverHost = HostBox.Text.Trim();
            cfg.ReceiverToken = TokenBox.Text.Trim();
            cfg.OutputName = OutputNameBox.Text.Trim();
            cfg.WriteImage = WriteImageBox.IsChecked == true;
            cfg.SkipOnErrors = SkipBox.IsChecked == true;
            cfg.SectorsPerRead = int.Parse(TagOf(SectorsPerReadBox, "32"));
            cfg.RetryReadSpeedKBps = int.Parse(TagOf(RetrySpeedBox, "1385"));
            cfg.DriveReadRetryCount = int.Parse(TagOf(DriveRetryBox, "-1"));

            int port, timeout, passes;
            long start, limit;
            if (!int.TryParse(PortBox.Text.Trim(), out port) || port < 1 || port > 65535) error = "Port must be a number from 1 to 65535.";
            else if (!int.TryParse(TimeoutBox.Text.Trim(), out timeout) || timeout < 5 || timeout > 120) error = "Timeout must be 5 to 120 seconds.";
            else if (!int.TryParse(RetryPassesBox.Text.Trim(), out passes) || passes < 0 || passes > 100) error = "Retry passes must be 0 to 100.";
            else if (!long.TryParse(StartBox.Text.Trim(), out start) || start < 0) error = "First sector must be 0 or more.";
            else if (!long.TryParse(LimitBox.Text.Trim(), out limit) || limit < 0) error = "Sectors must be 0 (whole disc) or more.";
            else if (cfg.SendToPc && cfg.ReceiverHost.Length == 0) error = "Enter the PC address, or choose LocalState as the destination.";
            else if (cfg.OutputName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) error = "The file name contains characters that are not allowed.";
            else
            {
                cfg.ReceiverPort = port;
                cfg.TimeoutSeconds = timeout;
                cfg.RetryPasses = passes;
                cfg.StartSector = start;
                cfg.SectorLimit = limit;
                return cfg;
            }
            return null;
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            int port;
            string host = HostBox.Text.Trim();
            if (host.Length == 0 || !int.TryParse(PortBox.Text.Trim(), out port))
            {
                TestText.Text = "Enter the PC address and port first.";
                return;
            }
            TestButton.IsEnabled = false;
            TestText.Text = "Connecting...";
            try
            {
                using (var client = new ReceiverClient(host, port, TokenBox.Text.Trim()))
                {
                    var o = await client.HelloAsync();
                    TestText.Text = "OK: the receiver saves to " + o.GetNamedString("out", "?");
                }
            }
            catch (ReceiverException ex) when (ex.Status == 401)
            {
                TestText.Text = "Connected, but the token is wrong.";
            }
            catch (Exception ex)
            {
                TestText.Text = "Failed: " + ex.Message + " (is the receiver running, and the firewall open?)";
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            box.SelectedIndex = 0;
            for (int i = 0; i < box.Items.Count; i++)
            {
                var item = box.Items[i] as ComboBoxItem;
                if (item != null && (item.Tag as string) == tag) { box.SelectedIndex = i; return; }
            }
        }

        private static string TagOf(ComboBox box, string fallback)
        {
            var item = box.SelectedItem as ComboBoxItem;
            return item != null && item.Tag is string ? (string)item.Tag : fallback;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusText.Text = "Cancelling... (finishing the current block)";
            }
        }
    }
}
