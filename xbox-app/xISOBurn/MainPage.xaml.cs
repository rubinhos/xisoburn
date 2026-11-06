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

        // Web page served by the app: the PC browser downloads the copies from it. The status fragment is
        // rebuilt on the UI thread once a second and read by the server threads (immutable strings, no
        // lock needed); _webCfg is replaced, never modified, when the settings are saved.
        private WebServer _web;
        private volatile string _webBody = "";
        private volatile CopyConfig _webCfg;
        private DispatcherTimer _webTimer;
        private string _freeSpace = "";
        private DateTime _freeSpaceAt = DateTime.MinValue;
        private string _webUrl = "";
        // Separate from the copy's DisplayRequest: keeps the screen on while the PC downloads an ISO, so
        // a download started after the copy finished is not cut by the console dimming or idling off.
        private DisplayRequest _downloadDisplay;

        public MainPage()
        {
            InitializeComponent();
            LogList.ItemsSource = _lines;
            _log = new Logger(_folder, "xisoburn", AddLine);
            _log.Info("xISOBurn started. Data folder: " + _folder);

            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            _webCfg = cfg;
            _log.Info(msg);
            ConfigText.Text = cfg.Describe();
            StatusText.Text = "Insert a disc and choose \"Detect disc\", then \"Copy / resume\". Download the copy from the web page shown above.";

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
            var cfg = _webCfg;
            try
            {
                _web = new WebServer(_log, _folder, () => _webBody, () => _webCfg, RemoteAsync);
                bool ok = await _web.StartAsync(cfg.WebPort);
                var addrs = WebServer.LocalAddresses();
                if (!ok || addrs.Count == 0)
                {
                    SubtitleText.Text = !ok ? "Web page unavailable on port " + cfg.WebPort + " - choose another port in Settings"
                                            : "No network connection - the web page needs the local network";
                    if (addrs.Count == 0) _log.Info("Web server: no IPv4 address found on the console's network adapters.");
                }
                else
                {
                    _webUrl = "http://" + addrs[0] + (cfg.WebPort == 80 ? "" : ":" + cfg.WebPort) + "/";
                    ShowWebAddress();
                    _log.Info("Web page (open in the PC browser): " + _webUrl + (addrs.Count > 1 ? "  (other addresses: " + string.Join(", ", addrs.GetRange(1, addrs.Count - 1)) + ")" : ""));
                }
            }
            catch (Exception ex)
            {
                _log.Info("Web server failed to start: " + ex.Message);
            }

            _webTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _webTimer.Tick += async (s, e) => { UpdateWebBody(); UpdateDownloadDisplay(); await RefreshFreeSpaceAsync(); };
            _webTimer.Start();
            UpdateWebBody();
        }

        private void UpdateDownloadDisplay()
        {
            int downloads = _web != null ? _web.ActiveDownloads : 0;
            if (downloads > 0 && _downloadDisplay == null)
            {
                _downloadDisplay = new DisplayRequest();
                _downloadDisplay.RequestActive();
                _log.Info("Keeping the screen on while the PC downloads (" + downloads + " download(s)).");
            }
            else if (downloads == 0 && _downloadDisplay != null)
            {
                _downloadDisplay.RequestRelease();
                _downloadDisplay = null;
                _log.Info("Downloads finished; the console may dim and idle as usual again.");
            }
        }

        private void ShowWebAddress()
        {
            if (_webUrl.Length == 0) return;
            var cfg = _webCfg;
            SubtitleText.Text = "On the PC, open " + _webUrl + (cfg.WebPinRequired ? "   PIN " + cfg.WebPin : "");
        }

        private async Task RefreshFreeSpaceAsync()
        {
            if (DateTime.UtcNow - _freeSpaceAt < TimeSpan.FromSeconds(15)) return;
            _freeSpaceAt = DateTime.UtcNow;
            try
            {
                var props = await ApplicationData.Current.LocalFolder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                object v;
                if (props.TryGetValue("System.FreeSpace", out v) && v is ulong)
                    _freeSpace = ((ulong)v / 1073741824.0).ToString("0.0") + " GiB free on the Xbox";
            }
            catch (Exception)
            {
                _freeSpace = "";
            }
        }

        private void UpdateWebBody()
        {
            Func<string, string> enc = System.Net.WebUtility.HtmlEncode;
            var sb = new System.Text.StringBuilder();
            sb.Append("<h2>Now on the Xbox</h2>");
            // The app's buttons, enabled exactly as on the console. The server checks again when one is pressed.
            // data-wait: the text of the full-screen loading shown until the Xbox answers.
            Func<string, string, string, Button, string> button = (action, label, wait, b) =>
                "<button name=\"do\" value=\"" + action + "\" data-wait=\"" + wait + "\"" +
                (b.IsEnabled && !_settingsOpen ? "" : " disabled") + ">" + label + "</button>";
            sb.Append("<form method=\"post\" action=\"/action\" class=\"controls\">")
              .Append(button("detect", "Detect disc", "Detecting the disc...", DetectButton))
              .Append(button("copy", "Copy / resume", "Starting the copy...", CopyButton))
              .Append(button("cancel", "Pause / cancel", "Pausing...", CancelButton))
              .Append(button("retry", "Retry failed", "Starting the retry pass...", RetryButton))
              .Append(button("scan", "Scan disc", "Starting the scan...", ScanButton))
              .Append(SettingsButton.IsEnabled && !_settingsOpen
                  ? "<a href=\"/settings\">Settings</a>"
                  : "<span class=\"dim\">Settings (unavailable while the app is busy or the dialog is open on the Xbox)</span>")
              .Append("</form>");
            sb.Append("<div class=\"box\"><b>Disc</b>\n").Append(enc(DiscText.Text)).Append("</div>");
            sb.Append("<div class=\"box\"><b>Progress</b>\n").Append(enc(ProgressText.Text)).Append("</div>");
            sb.Append("<div class=\"box\">").Append(enc(StatusText.Text)).Append("</div>");
            if (_freeSpace.Length > 0) sb.Append("<div class=\"dim\">").Append(enc(_freeSpace)).Append("</div>");
            sb.Append("<details id=\"recentlog\"><summary>Recent log</summary><pre>");
            int start = Math.Max(0, _lines.Count - 60);
            for (int i = start; i < _lines.Count; i++) sb.Append(enc(_lines[i].Text)).Append('\n');
            sb.Append("</pre></details>");
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
                StatusText.Text = ok ? "Disc detected. Choose \"Copy / resume\"." : "No disc detected (see log).";
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
                StatusText.Text = "Copying to the Xbox" +
                                  (_probe.Cpst == 1 && cfg.DecryptCss ? " (CSS disc: decrypting the video)" : "") +
                                  "... do not leave the app. B pauses (progress is kept). " +
                                  "The download can already start on the web page.";
                Bar.Value = 0;
                _refusalShown = false;

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
                                      (_webUrl.Length > 0 ? " Download it from " + _webUrl : "") +
                                      (result.Bad > 0 ? " \"Retry failed\" tries the bad sectors once more." : "");
                else if (result.Cancelled)
                    StatusText.Text = "Paused: " + counts + ". Choose \"Copy / resume\" to continue.";
                else if (result.Error != null)
                    StatusText.Text = "Stopped: " + result.Error + " (" + counts + ")";
                else
                    StatusText.Text = "Range done: " + counts + ".";
                if (result.CssDecrypting)
                    StatusText.Text += " CSS: " + result.CssDescrambled.ToString("N0") + " sector(s) decrypted" +
                                       (result.CssLeftScrambled > 0 ? ", " + result.CssLeftScrambled.ToString("N0") + " left scrambled (see log)" : "") + ".";
                if (result.RefusedThisRun >= RescueJob.RefusalNoticeAfter)
                    StatusText.Text += " " + RefusalMessage(result.RefusedThisRun);
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
                " | not tried " + p.Untried.ToString("N0") +
                (p.Refused > 0 ? " | refused " + p.Refused.ToString("N0") : "");
            if (p.Refused >= RescueJob.RefusalNoticeAfter && !_refusalShown)
            {
                _refusalShown = true;
                StatusText.Text = RefusalMessage(p.Refused) + " B pauses the copy.";
            }
        }

        private bool _refusalShown;

        private string RefusalMessage(long refused)
        {
            return "The drive refused " + refused.ToString("N0") + " read(s) at once: that is copy protection or a " +
                   "region mismatch, not disc damage" +
                   (_probe != null && _probe.Cpst == 1 ? " (check the CSS section of the log: the disc's region must match the console's)" : "") +
                   ". Retrying will not recover those sectors.";
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

        private bool _restartNeeded;

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            WebPortBox.Text = cfg.WebPort.ToString();
            PinRequiredBox.IsChecked = cfg.WebPinRequired;
            PinBox.Text = cfg.WebPin;
            OutputNameBox.Text = cfg.OutputName;
            WriteImageBox.IsChecked = cfg.WriteImage;
            DecryptCssBox.IsChecked = cfg.DecryptCss;
            SelectByTag(SectorsPerReadBox, cfg.SectorsPerRead.ToString());
            TimeoutBox.Text = cfg.TimeoutSeconds.ToString();
            SkipBox.IsChecked = cfg.SkipOnErrors;
            RetryPassesBox.Text = cfg.RetryPasses.ToString();
            SelectByTag(RetrySpeedBox, cfg.RetryReadSpeedKBps.ToString());
            SelectByTag(DriveRetryBox, cfg.DriveReadRetryCount.ToString());
            StartBox.Text = cfg.StartSector.ToString();
            LimitBox.Text = cfg.SectorLimit.ToString();
            SettingsError.Text = "";
            _restartNeeded = false;
            // While the dialog is open the web page cannot change the settings (one would overwrite the other).
            _settingsOpen = true;
            try { await SettingsDialog.ShowAsync(); }
            finally { _settingsOpen = false; }

            if (_restartNeeded)
            {
                // The only setting that cannot change while the app runs: the web server is already
                // listening on the old port.
                var notice = new ContentDialog
                {
                    Title = "Restart needed",
                    Content = "The web page will use port " + _webCfg.WebPort + " after the app is closed and opened again.\n" +
                              "Until then it stays at " + (_web != null && _web.Port > 0 ? _web.Port.ToString() : "the old port") + ".",
                    CloseButtonText = "OK",
                };
                await notice.ShowAsync();
            }
            SettingsButton.Focus(FocusState.Programmatic);
        }

        private void NewPinButton_Click(object sender, RoutedEventArgs e)
        {
            PinBox.Text = CopyConfig.NewPin();
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
            StatusText.Text = ApplySettings(cfg, "the console");
        }

        /// <summary>
        /// Saves the settings (from the console dialog or the web page) and applies what can change at once.
        /// Returns the confirmation for the status line or the web page.
        /// </summary>
        private string ApplySettings(CopyConfig cfg, string from)
        {
            cfg.Save(_folder);
            string msg;
            cfg = CopyConfig.Load(_folder, out msg); // normalises the values exactly as a run will see them
            _webCfg = cfg;                            // PIN changes apply to the web page at once
            ShowWebAddress();
            ConfigText.Text = cfg.Describe();
            _log.Info("Settings saved from " + from + ": " + cfg.Describe());
            _restartNeeded = _web != null && _web.Port > 0 && cfg.WebPort != _web.Port;
            return _restartNeeded ? "Settings saved. Restart the app to use the new web page port (" + cfg.WebPort + ")."
                                  : "Settings saved. They apply from the next run.";
        }

        /// <summary>Builds a config from the dialog; null (with a message) if a field is invalid.</summary>
        private CopyConfig ReadSettings(out string error)
        {
            string msg;
            var form = new Dictionary<string, string>
            {
                { "WebPort", WebPortBox.Text },
                { "WebPinRequired", PinRequiredBox.IsChecked == true ? "true" : "false" },
                { "WebPin", PinBox.Text },
                { "OutputName", OutputNameBox.Text },
                { "WriteImage", WriteImageBox.IsChecked == true ? "true" : "false" },
                { "DecryptCss", DecryptCssBox.IsChecked == true ? "true" : "false" },
                { "SectorsPerRead", TagOf(SectorsPerReadBox, "32") },
                { "TimeoutSeconds", TimeoutBox.Text },
                { "SkipOnErrors", SkipBox.IsChecked == true ? "true" : "false" },
                { "RetryPasses", RetryPassesBox.Text },
                { "RetryReadSpeedKBps", TagOf(RetrySpeedBox, "1385") },
                { "DriveReadRetryCount", TagOf(DriveRetryBox, "-1") },
                { "StartSector", StartBox.Text },
                { "SectorLimit", LimitBox.Text },
            };
            return CopyConfig.FromForm(CopyConfig.Load(_folder, out msg), form, out error);
        }

        // ------------------------------------------------------------ remote control (web page)

        private bool _settingsOpen;

        /// <summary>
        /// Called by the web server (on a thread-pool thread) when a button is pressed on the web page.
        /// Runs on the UI thread, exactly like pressing the button on the console: the same handlers, the
        /// same enabled/disabled rules. Long actions (a copy) are started, not waited for.
        /// </summary>
        private Task<RemoteReply> RemoteAsync(string action, IDictionary<string, string> form, string remote)
        {
            var done = new TaskCompletionSource<RemoteReply>();
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { done.TrySetResult(Remote(action, form, remote)); }
                catch (Exception ex) { done.TrySetResult(RemoteReply.Refused("Error: " + ex.Message)); }
            });
            return done.Task;
        }

        private RemoteReply Remote(string action, IDictionary<string, string> form, string remote)
        {
            if (_settingsOpen)
                return RemoteReply.Refused("The Settings dialog is open on the Xbox. Close it there first.");
            Button button;
            string label;
            switch (action)
            {
                case "detect": button = DetectButton; label = "Detect disc"; break;
                case "copy": button = CopyButton; label = "Copy / resume"; break;
                case "retry": button = RetryButton; label = "Retry failed"; break;
                case "scan": button = ScanButton; label = "Scan disc"; break;
                case "cancel": button = CancelButton; label = "Pause / cancel"; break;
                case "settings":
                {
                    if (!SettingsButton.IsEnabled)
                        return RemoteReply.Refused("The app is busy; the settings can be changed when it is idle.");
                    string error, msg;
                    var cfg = CopyConfig.FromForm(CopyConfig.Load(_folder, out msg), form, out error);
                    if (cfg == null) return RemoteReply.Refused(error);
                    string result = ApplySettings(cfg, "the web page (" + remote + ")");
                    StatusText.Text = result;
                    return RemoteReply.Done(result);
                }
                default:
                    return RemoteReply.Refused("Unknown action.");
            }

            if (!button.IsEnabled)
                return RemoteReply.Refused("\"" + label + "\" is not available right now" +
                                           (_busy ? " (the app is busy)." : action == "retry" ? " (no failed sectors to retry)." : "."));
            _log.Info("Web: " + remote + " pressed \"" + label + "\".");
            switch (action)
            {
                case "detect": DetectButton_Click(button, null); break;
                case "copy": CopyButton_Click(button, null); break;
                case "retry": RetryButton_Click(button, null); break;
                case "scan": ScanButton_Click(button, null); break;
                case "cancel": CancelButton_Click(button, null); break;
            }
            return RemoteReply.Done(null);
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
