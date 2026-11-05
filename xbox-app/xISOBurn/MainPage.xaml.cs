using System;
using System.Collections.ObjectModel;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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
        private const int MaxUiLines = 5000;
        private readonly ObservableCollection<LogLine> _lines = new ObservableCollection<LogLine>();
        private bool _running;

        public MainPage()
        {
            InitializeComponent();
            LogList.ItemsSource = _lines;
            StatusText.Text = "Coloque o disco no drive e pressione A. Logs: " + ApplicationData.Current.LocalFolder.Path;
            Loaded += (s, e) => RunButton.Focus(FocusState.Programmatic);
        }

        private void AddLine(string line)
        {
            if (!Dispatcher.HasThreadAccess)
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => AddLine(line));
                return;
            }
            if (_lines.Count >= MaxUiLines) _lines.RemoveAt(0);
            var item = new LogLine(line);
            _lines.Add(item);
            LogList.ScrollIntoView(item);
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            _running = true;
            RunButton.IsEnabled = false;
            _lines.Clear();
            StatusText.Text = "Executando... não saia do app.";

            // Mantém a tela ligada: no Xbox, app que perde o foco ou deixa a tela apagar é suspenso.
            var display = new DisplayRequest();
            display.RequestActive();
            Logger log = null;
            try
            {
                string folder = ApplicationData.Current.LocalFolder.Path;
                log = new Logger(folder, "spike", AddLine);
                await new SpikeRunner(log, folder).RunAsync();
                StatusText.Text = "Concluído. Log: " + log.FilePath;
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
                RunButton.IsEnabled = true;
                _running = false;
                RunButton.Focus(FocusState.Programmatic);
            }
        }
    }
}
