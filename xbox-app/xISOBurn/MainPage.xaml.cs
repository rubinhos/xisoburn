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
    /// <summary>Uma linha do log na tela (objeto próprio para ScrollIntoView funcionar com linhas repetidas).</summary>
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

        // O drive é aberto e usado sempre em threads do pool (MTA), como o XBSX2 faz; a UI só espera.
        private OpticalDrive _drive;
        private DiscProbe _probe;
        private CancellationTokenSource _cts;
        private bool _busy;

        public MainPage()
        {
            InitializeComponent();
            LogList.ItemsSource = _lines;
            _log = new Logger(_folder, "xisoburn", AddLine);
            _log.Info("xISOBurn iniciado. Pasta de dados: " + _folder);

            string msg;
            var cfg = CopyConfig.Load(_folder, out msg);
            _log.Info(msg);
            ConfigText.Text = cfg.Describe();
            StatusText.Text = "Coloque o disco e use \"Detectar disco\". Arquivos e logs: " + _folder;

            Loaded += (s, e) => DetectButton.Focus(FocusState.Programmatic);
            // No Xbox, o botão B gera BackRequested. Sem tratar, o sistema pode fechar o app no meio da cópia.
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = true;
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusText.Text = "Cancelando... (termina o bloco atual)";
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
                _log.Info("Drive aberto: " + _drive.DeviceId);
                return true;
            }
            catch (Exception ex)
            {
                _log.Info("Não foi possível abrir o drive: 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                StatusText.Text = "Drive não encontrado ou acesso negado (ver log).";
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
                CloseDrive(); // o handle pode ter ficado inválido; reabre na próxima vez
            return _probe.Ready && _probe.TotalSectors > 0;
        }

        private async void DetectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, false);
            try
            {
                bool ok = await DetectAsync(20);
                StatusText.Text = ok ? "Disco detectado. Use \"Copiar para LocalState\"." : "Disco não detectado (ver log).";
            }
            catch (Exception ex)
            {
                _log.Info("ERRO na detecção: " + ex);
                StatusText.Text = "Erro: " + ex.Message;
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
                // Relê a configuração a cada cópia, para valer o que foi editado pelo Device Portal.
                string msg;
                var cfg = CopyConfig.Load(_folder, out msg);
                _log.Info(msg);
                ConfigText.Text = cfg.Describe();

                // Detecta de novo: o disco pode ter sido trocado desde a última detecção.
                if (!await DetectAsync((uint)cfg.TimeoutSeconds))
                {
                    StatusText.Text = "Sem disco pronto para copiar (ver log).";
                    return;
                }

                // Mantém a tela ligada: no Xbox, app que sai da tela ou fica com a tela apagada é suspenso.
                display.RequestActive();
                displayActive = true;
                _cts = new CancellationTokenSource();
                SetBusy(true, true);
                StatusText.Text = "Copiando... não saia do app. B cancela.";
                Bar.Value = 0;

                var progress = new Progress<CopyProgress>(UpdateProgress);
                var job = new CopyJob(_drive, _probe, cfg, _log, _folder);
                var token = _cts.Token;
                var result = await Task.Run(() => job.RunAsync(progress, token));

                if (result.Completed)
                    StatusText.Text = "Concluído: " + result.BadSectors + " setor(es) não lido(s). SHA-256 " + result.Sha256 +
                                      (result.ImagePath != null ? " | " + result.ImagePath : "");
                else if (result.Cancelled)
                    StatusText.Text = "Cancelado. Mapa parcial: " + result.MapPath;
                else
                    StatusText.Text = "Interrompido: " + result.Error;
            }
            catch (Exception ex)
            {
                _log.Info("ERRO na cópia: " + ex);
                StatusText.Text = "Erro: " + ex.Message;
            }
            finally
            {
                if (displayActive) display.RequestRelease();
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                SetBusy(false, false);
                CopyButton.Focus(FocusState.Programmatic);
            }
        }

        private void UpdateProgress(CopyProgress p)
        {
            double pct = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
            Bar.Value = pct;
            ProgressText.Text =
                p.Done.ToString("N0") + " / " + p.Total.ToString("N0") + " setores (" + pct.ToString("0.0") + "%) | LBA " + p.CurrentLba.ToString("N0") + "\n" +
                "Velocidade: " + (p.BytesPerSecond / 1048576.0).ToString("0.0") + " MiB/s | Decorrido: " + CopyJob.FormatTime(p.Elapsed) +
                " | Restante: " + (p.Remaining.HasValue ? CopyJob.FormatTime(p.Remaining.Value) : "-") + "\n" +
                "Setores não lidos (zeros): " + p.Bad.ToString("N0");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                StatusText.Text = "Cancelando... (termina o bloco atual)";
            }
        }

        private async void SpikeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true, false);
            CloseDrive(); // o spike abre o próprio handle
            _lines.Clear();
            StatusText.Text = "Executando o teste de viabilidade... não saia do app.";
            var display = new DisplayRequest();
            display.RequestActive();
            Logger log = null;
            try
            {
                log = new Logger(_folder, "spike", AddLine);
                await new SpikeRunner(log, _folder).RunAsync();
                StatusText.Text = "Teste concluído. Log: " + log.FilePath;
            }
            catch (Exception ex)
            {
                AddLine("ERRO: " + ex);
                StatusText.Text = "Erro: " + ex.Message;
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
