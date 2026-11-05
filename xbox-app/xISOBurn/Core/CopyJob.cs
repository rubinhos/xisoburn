using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    internal sealed class CopyProgress
    {
        public long Done;          // setores processados (lidos ou preenchidos com zeros)
        public long Total;         // setores do trecho
        public long Bad;           // setores preenchidos com zeros
        public long CurrentLba;
        public double BytesPerSecond;
        public TimeSpan Elapsed;
        public TimeSpan? Remaining;
    }

    internal sealed class CopyResult
    {
        public bool Completed;
        public bool Cancelled;
        public string Error;
        public long SectorsDone;
        public long BadSectors;
        public int BadRanges;
        public string Sha256;
        public string ImagePath;
        public string MapPath;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Marco 2: lê o disco inteiro (ou o trecho configurado) para um arquivo em LocalState.
    /// Calcula o SHA-256 durante a leitura, sobre exatamente os bytes gravados (incluindo os zeros dos
    /// setores ruins), e grava o .map e o .sha256 ao lado da imagem.
    /// Memória: um único buffer de no máximo 64 setores (128 KiB); o disco nunca fica na memória.
    /// </summary>
    internal sealed class CopyJob
    {
        private const int SectorSize = OpticalDrive.SectorSize;

        private readonly OpticalDrive _drive;
        private readonly DiscProbe _probe;
        private readonly CopyConfig _cfg;
        private readonly Logger _log;
        private readonly string _folder;

        public CopyJob(OpticalDrive drive, DiscProbe probe, CopyConfig cfg, Logger log, string folder)
        {
            _drive = drive;
            _probe = probe;
            _cfg = cfg;
            _log = log;
            _folder = folder;
        }

        public async Task<CopyResult> RunAsync(IProgress<CopyProgress> progress, CancellationToken ct)
        {
            var result = new CopyResult();
            long first = Math.Min(_cfg.StartSector, _probe.TotalSectors);
            long end = _probe.TotalSectors;
            if (_cfg.SectorLimit > 0) end = Math.Min(end, first + _cfg.SectorLimit);
            long total = end - first;
            if (total <= 0)
            {
                result.Error = "Nada a copiar (início " + first + ", fim " + end + ").";
                return result;
            }

            string baseName = BuildName(first, end);
            string imagePath = Path.Combine(_folder, baseName + ".iso");
            result.MapPath = Path.Combine(_folder, baseName + ".map");

            _log.Section("Cópia");
            _log.Info("Trecho: LBA " + first + " a " + (end - 1) + " (" + total + " setores, " + DiscProbe.FormatSize(total) + ")");
            _log.Info("Configuração: " + _cfg.Describe());
            _log.Info(_cfg.WriteImage ? "Imagem: " + imagePath : "Modo só leitura: a imagem não será gravada.");

            var map = new BadSectorMap();
            var reader = new SectorReader(_drive, _cfg, _log);
            CryptographicHash hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256).CreateHash();
            var buffer = new byte[_cfg.SectorsPerRead * SectorSize];
            FileStream file = null;
            long done = 0;
            var sw = Stopwatch.StartNew();
            var speed = new SpeedMeter();
            TimeSpan lastReport = TimeSpan.Zero;
            int lastPercentLogged = -1;

            try
            {
                if (_cfg.WriteImage)
                {
                    file = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                    // Reserva o tamanho final logo no começo: se faltar espaço, falha agora, e não horas depois.
                    file.SetLength(total * SectorSize);
                }

                for (long lba = first; lba < end; )
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(_cfg.SectorsPerRead, end - lba);
                    var failed = await reader.ReadAsync(lba, n, buffer, ct);
                    foreach (var f in failed) map.Add(f.Lba, f.Error);

                    int bytes = n * SectorSize;
                    hash.Append(buffer.AsBuffer(0, bytes));
                    if (file != null) file.Write(buffer, 0, bytes);

                    lba += n;
                    done += n;

                    var elapsed = sw.Elapsed;
                    speed.Add(elapsed, done);
                    if (elapsed - lastReport >= TimeSpan.FromMilliseconds(250) || done == total)
                    {
                        lastReport = elapsed;
                        progress.Report(MakeProgress(done, total, map.TotalSectors, lba, elapsed, speed));
                    }
                    int percent = (int)(done * 100 / total);
                    if (percent != lastPercentLogged && (percent % 5 == 0))
                    {
                        lastPercentLogged = percent;
                        _log.Info(percent + "% - LBA " + lba + " | " + (speed.BytesPerSecond / 1048576.0).ToString("0.0") +
                                  " MiB/s | ruins: " + map.TotalSectors + " | " + FormatTime(elapsed));
                    }
                }

                if (file != null) file.Flush();
                result.Completed = true;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                _log.Info("Cópia cancelada no LBA " + (first + done) + ".");
            }
            catch (MediumRemovedException ex)
            {
                result.Error = ex.Message;
                _log.Info("PARADA: " + ex.Message);
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message + " (0x" + ex.HResult.ToString("X8") + ")";
                _log.Info("ERRO: " + result.Error);
            }
            finally
            {
                if (file != null)
                {
                    try
                    {
                        // Se não terminou, corta o arquivo no ponto processado (o resto seria só zeros reservados).
                        if (!result.Completed) file.SetLength(done * SectorSize);
                        file.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _log.Info("Erro ao fechar a imagem: " + ex.Message);
                    }
                }
            }

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.SectorsDone = done;
            result.BadSectors = map.TotalSectors;
            result.BadRanges = map.Ranges.Count;
            result.ImagePath = _cfg.WriteImage ? imagePath : null;
            // O hash cobre exatamente o que foi processado; se a cópia não terminou, é o hash do trecho parcial.
            result.Sha256 = CryptographicBuffer.EncodeToHexString(hash.GetValueAndReset());

            try
            {
                File.WriteAllText(result.MapPath, map.ToText(_probe.VolumeLabel, first, end, done, result.Completed, result.Sha256),
                    new UTF8Encoding(false));
                if (_cfg.WriteImage && result.Completed)
                    File.WriteAllText(Path.Combine(_folder, baseName + ".sha256"), result.Sha256 + " *" + baseName + ".iso\n",
                        new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Info("Erro ao gravar o mapa: " + ex.Message);
            }

            _log.Section("Resultado da cópia");
            _log.Info(result.Completed ? "Concluída." : result.Cancelled ? "Cancelada." : "Interrompida: " + result.Error);
            _log.Info("Setores processados: " + done + " de " + total + " em " + FormatTime(result.Elapsed) +
                      " (média " + (done * (double)SectorSize / 1048576.0 / Math.Max(1, result.Elapsed.TotalSeconds)).ToString("0.0") + " MiB/s)");
            _log.Info("Comandos de leitura: " + reader.Commands + " | blocos recuperados com nova tentativa: " + reader.RecoveredBlocks +
                      " | setores recuperados: " + reader.RecoveredSectors);
            _log.Info("Setores NÃO lidos (zeros): " + map.TotalSectors + " em " + map.Ranges.Count + " intervalo(s)");
            foreach (var r in map.Ranges)
                _log.Info("  " + r.Start + "-" + r.End + " (" + r.Count + ") " + r.LastError);
            _log.Info("SHA-256" + (result.Completed ? "" : " (parcial)") + ": " + result.Sha256);
            _log.Info("Mapa: " + result.MapPath);
            return result;
        }

        private string BuildName(long first, long end)
        {
            string name = _cfg.OutputName;
            if (string.IsNullOrWhiteSpace(name)) name = _probe.VolumeLabel;
            if (string.IsNullOrWhiteSpace(name)) name = "disco";
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            name = sb.ToString();
            if (first > 0 || end < _probe.TotalSectors) name += "_lba" + first + "-" + (end - 1);
            return name;
        }

        private static CopyProgress MakeProgress(long done, long total, long bad, long lba, TimeSpan elapsed, SpeedMeter speed)
        {
            var p = new CopyProgress { Done = done, Total = total, Bad = bad, CurrentLba = lba, Elapsed = elapsed, BytesPerSecond = speed.BytesPerSecond };
            if (speed.BytesPerSecond > 0)
                p.Remaining = TimeSpan.FromSeconds((total - done) * (double)SectorSize / speed.BytesPerSecond);
            return p;
        }

        public static string FormatTime(TimeSpan t)
        {
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        /// <summary>Velocidade média dos últimos ~15 s (mais estável que a instantânea, mais atual que a média geral).</summary>
        private sealed class SpeedMeter
        {
            private readonly Queue<KeyValuePair<TimeSpan, long>> _samples = new Queue<KeyValuePair<TimeSpan, long>>();
            public double BytesPerSecond { get; private set; }

            public void Add(TimeSpan at, long sectorsDone)
            {
                _samples.Enqueue(new KeyValuePair<TimeSpan, long>(at, sectorsDone));
                while (_samples.Count > 2 && at - _samples.Peek().Key > TimeSpan.FromSeconds(15)) _samples.Dequeue();
                var oldest = _samples.Peek();
                double secs = (at - oldest.Key).TotalSeconds;
                if (secs > 0.5) BytesPerSecond = (sectorsDone - oldest.Value) * (double)SectorSize / secs;
            }
        }
    }
}
