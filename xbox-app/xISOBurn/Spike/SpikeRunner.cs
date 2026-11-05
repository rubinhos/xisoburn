using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Spike
{
    /// <summary>
    /// Marco 1: teste de viabilidade. Só LÊ informações e setores; não grava nada no disco/drive.
    /// Cada etapa é independente: se uma falhar, o resto continua, para o log ficar o mais completo possível.
    /// </summary>
    internal sealed class SpikeRunner
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private const uint Timeout = 30;          // segundos por comando (setor ruim pode demorar)
        private const int SafeChunk = 16;         // setores por READ(10) nas leituras "seguras"
        private const int FirstSectors = 1000;    // pedido no Marco 1
        private static readonly int[] TransferTests = { 16, 31, 32, 48, 64 };

        private readonly Logger _log;
        private readonly string _folder;
        private OpticalDrive _drive;

        // Resultados guardados para o resumo final.
        private int _profile = -1;
        private long _capacity = -1;
        private DvdPhysicalFormat _pfi0, _pfi1;
        private int _cpst = -1;
        private readonly List<string> _summary = new List<string>();

        public SpikeRunner(Logger log, string localFolder)
        {
            _log = log;
            _folder = localFolder;
        }

        public async Task RunAsync()
        {
            _log.Info("xISOBurn - teste de viabilidade (Marco 1)");
            _log.Info("Log: " + _log.FilePath);

            if (!await OpenDriveAsync()) return;
            try
            {
                await Step("INQUIRY", InquiryAsync);
                bool ready = false;
                await Step("TEST UNIT READY", async () => { ready = await TestUnitReadyAsync(); });
                if (!ready)
                {
                    _log.Info("Drive não ficou pronto. Coloque o disco, espere parar de girar e rode de novo.");
                    _log.Info("(Mesmo assim, as próximas etapas vão ser tentadas.)");
                }
                await Step("GET CONFIGURATION (tipo de mídia)", GetConfigurationAsync);
                await Step("READ CAPACITY(10)", ReadCapacityAsync);
                await Step("READ DVD STRUCTURE formato 0 (Physical Format)", ReadPhysicalFormatAsync);
                await Step("READ DVD STRUCTURE formato 1 (Copyright)", ReadCopyrightAsync);
                await Step("REPORT KEY formato 8 (estado de região RPC do drive)", ReportRpcStateAsync);
                if (MmcProfiles.IsCd(_profile))
                    await Step("READ TOC (CD)", ReadTocAsync);
                CompareSizes();
                await Step("Teste de tamanho de transferência", TransferSizeTestAsync);
                byte[] first = null;
                await Step("Leitura dos primeiros " + FirstSectors + " setores", async () => { first = await ReadFirstSectorsAsync(); });
                if (first != null)
                    await Step("Sistema de arquivos (ISO9660/UDF) e hexdump", () => { AnalyzeFileSystem(first); return Task.FromResult(0); });
                await Step("Leituras de amostra (meio do disco, troca de camada, fim)", SampleReadsAsync);
            }
            finally
            {
                _drive.Dispose();
            }

            _log.Section("RESUMO");
            foreach (var s in _summary) _log.Info(s);
            _log.Info("");
            _log.Info("Fim do teste. Baixe o log pelo Device Portal (File explorer > LocalAppData > xISOBurn_... > LocalState).");
        }

        private async Task Step(string title, Func<Task> body)
        {
            _log.Section(title);
            try { await body(); }
            catch (Exception ex)
            {
                _log.Info("EXCEÇÃO: 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
                _summary.Add(title + ": EXCEÇÃO " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- abertura

        private async Task<bool> OpenDriveAsync()
        {
            _log.Section("Procurando o drive (DeviceInformation.FindAllAsync)");
            _log.Info("Filtro AQS: " + OpticalDrive.Selector);
            IReadOnlyList<Windows.Devices.Enumeration.DeviceInformation> devices;
            try
            {
                devices = await OpticalDrive.FindAsync();
            }
            catch (Exception ex)
            {
                _log.Info("FALHA na enumeração: 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                _summary.Add("Enumeração: FALHOU (" + ex.Message + ")");
                return false;
            }

            _log.Info("Interfaces encontradas: " + devices.Count);
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                object enabled;
                d.Properties.TryGetValue("System.Devices.InterfaceEnabled", out enabled);
                _log.Info("  [" + i + "] nome=\"" + d.Name + "\" IsEnabled=" + d.IsEnabled + " InterfaceEnabled=" + (enabled ?? "?"));
                _log.Info("      id=" + d.Id);
            }
            if (devices.Count == 0)
            {
                _log.Info("Nenhuma interface CD-ROM. Console sem drive (Series S/Digital) ou capacidade negada.");
                _summary.Add("Drive: NÃO ENCONTRADO");
                return false;
            }

            _log.Section("Abrindo o drive (CustomDevice.FromIdAsync, ReadWrite, Shared)");
            // Igual ao XBSX2: usa a primeira interface. Buffers para até 64 setores (128 KiB) para o
            // teste de tamanho de transferência; o XBSX2 usa 64 KiB e limita a 31 setores por comando.
            try
            {
                _drive = await OpticalDrive.OpenAsync(devices[0].Id, 64 * SectorSize);
            }
            catch (Exception ex)
            {
                _log.Info("FALHA ao abrir: 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
                _summary.Add("Abrir drive: FALHOU (0x" + ex.HResult.ToString("X8") + ")");
                return false;
            }
            _log.Info("Aberto. Código IOCTL = 0x" + _drive.ControlCode.ToString("X8") + " (esperado 0x0004D004 = IOCTL_SCSI_PASS_THROUGH)");
            _summary.Add("Drive: aberto (" + devices.Count + " interface(s))");
            return true;
        }

        // ---------------------------------------------------------------- informações

        private async Task InquiryAsync()
        {
            var buf = new byte[36];
            var r = await _drive.SendAsync(ScsiCommands.Inquiry(36), 36, buf, 0, Timeout);
            _log.Info("Resultado: " + r.Describe());
            if (!r.Success || r.DataLength < 36) return;
            string vendor = Bytes.Ascii(buf, 8, 8), product = Bytes.Ascii(buf, 16, 16), rev = Bytes.Ascii(buf, 32, 4);
            _log.Info("Tipo de periférico: 0x" + (buf[0] & 0x1F).ToString("X2") + " (0x05 = CD/DVD)");
            _log.Info("Fabricante: " + vendor + " | Modelo: " + product + " | Firmware: " + rev);
            _summary.Add("Drive: " + vendor + " " + product + " fw " + rev);
        }

        private async Task<bool> TestUnitReadyAsync()
        {
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                var r = await _drive.SendAsync(ScsiCommands.TestUnitReady(), 0, null, 0, Timeout);
                _log.Info("Tentativa " + attempt + ": " + r.Describe());
                if (r.Success)
                {
                    _summary.Add("TEST UNIT READY: pronto");
                    return true;
                }
                // Esperar só nos casos "transitórios": disco girando ou aviso de troca de mídia/reset.
                bool transient = r.Sense != null &&
                    ((r.Sense.Key == 0x02 && r.Sense.Asc == 0x04) || r.Sense.Key == 0x06);
                if (!transient) break;
                await Task.Delay(1000);
            }
            _summary.Add("TEST UNIT READY: NÃO pronto");
            return false;
        }

        private async Task GetConfigurationAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.GetConfiguration(8), 8, buf, 0, Timeout);
            _log.Info("Resultado: " + r.Describe());
            if (!r.Success || r.DataLength < 8) return;
            _profile = Bytes.BE16(buf, 6);
            _log.Info("Perfil atual: 0x" + _profile.ToString("X4") + " = " + MmcProfiles.Name(_profile));
            _summary.Add("Mídia: " + MmcProfiles.Name(_profile) + " (perfil 0x" + _profile.ToString("X4") + ")");
        }

        private async Task ReadCapacityAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.ReadCapacity10(), 8, buf, 0, Timeout);
            _log.Info("Resultado: " + r.Describe());
            if (!r.Success || r.DataLength < 8) return;
            uint lastLba = Bytes.BE32(buf, 0);
            uint blockLen = Bytes.BE32(buf, 4);
            _capacity = (long)lastLba + 1;
            _log.Info("Último LBA: " + lastLba + " | Tamanho do bloco: " + blockLen + " bytes");
            _log.Info("Capacidade: " + _capacity + " setores = " + FormatBytes(_capacity * SectorSize));
            if (blockLen != SectorSize)
                _log.Info("ATENÇÃO: bloco diferente de 2048 bytes.");
            _summary.Add("READ CAPACITY: " + _capacity + " setores (" + FormatBytes(_capacity * SectorSize) + ")");
        }

        private async Task ReadPhysicalFormatAsync()
        {
            _pfi0 = await ReadPfiLayerAsync(0);
            if (_pfi0 == null) return;
            if (_pfi0.NumberOfLayers > 1)
            {
                // A camada 1 só tem descritor próprio relevante em PTP, mas pedimos sempre para registrar.
                _log.Info("");
                _log.Info("Disco de 2 camadas: lendo o descritor da camada 1.");
                _pfi1 = await ReadPfiLayerAsync(1);
            }

            string layers = _pfi0.NumberOfLayers == 1 ? "1 camada"
                : "2 camadas, " + (_pfi0.OppositeTrackPath ? "OTP" : "PTP");
            _summary.Add("DVD: " + _pfi0.BookTypeName + ", " + layers + ", " + _pfi0.TotalSectors + " setores pela estrutura" +
                (_pfi0.NumberOfLayers > 1 ? ", camada 0 = " + _pfi0.Layer0Sectors + " setores" : ""));
        }

        private async Task<DvdPhysicalFormat> ReadPfiLayerAsync(byte layer)
        {
            // 36 bytes, como no XBSX2 (cabeçalho de 4 + descritor de 17 + folga).
            var buf = new byte[36];
            var r = await _drive.SendAsync(ScsiCommands.ReadDvdStructure(layer, 0x00, buf.Length), buf.Length, buf, 0, Timeout);
            _log.Info("Camada " + layer + ": " + r.Describe());
            if (!r.Success) return null;
            foreach (var line in HexDump.Lines(buf, 0, r.DataLength, 0)) _log.Info("  " + line);
            var p = DvdPhysicalFormat.Parse(buf, r.DataLength);
            if (p == null) { _log.Info("Resposta curta demais para interpretar."); return null; }
            _log.Info("  Tipo de livro: " + p.BookTypeName + " (versão " + p.PartVersion + ")");
            _log.Info("  Tamanho: " + (p.DiscSize == 0 ? "120 mm" : p.DiscSize == 1 ? "80 mm" : "código " + p.DiscSize) +
                      " | Taxa máx.: " + p.MaxRateName);
            _log.Info("  Camadas: " + p.NumberOfLayers + " | Trilha: " + (p.OppositeTrackPath ? "OTP (oposta)" : "PTP (paralela)") +
                      " | Tipo de camada: 0x" + p.LayerType.ToString("X"));
            _log.Info("  Setor físico inicial: 0x" + p.StartSector.ToString("X6") +
                      " | final: 0x" + p.EndSector.ToString("X6") +
                      " | fim da camada 0: 0x" + p.EndSectorLayer0.ToString("X6"));
            _log.Info("  BCA: " + (p.Bca ? "sim" : "não"));
            _log.Info("  => setores na camada 0: " + p.Layer0Sectors + " | total calculado: " + p.TotalSectors);
            return p;
        }

        private async Task ReadCopyrightAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.ReadDvdStructure(0, 0x01, buf.Length), buf.Length, buf, 0, Timeout);
            _log.Info("Resultado: " + r.Describe());
            if (!r.Success || r.DataLength < 6) return;
            _cpst = buf[4];
            int rmi = buf[5];
            string system;
            switch (_cpst)
            {
                case 0x00: system = "nenhuma"; break;
                case 0x01: system = "CSS/CPPM"; break;
                case 0x02: system = "CPRM"; break;
                default: system = "código 0x" + _cpst.ToString("X2"); break;
            }
            _log.Info("Proteção do disco (CPST): " + system);
            _log.Info("Regiões do disco (RMI 0x" + rmi.ToString("X2") + "): " + RegionList(rmi));
            _summary.Add("Proteção: " + system + " | regiões do disco: " + RegionList(rmi));
        }

        private async Task ReportRpcStateAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.ReportKeyRpcState(), buf.Length, buf, 0, Timeout);
            _log.Info("Resultado: " + r.Describe());
            if (!r.Success || r.DataLength < 7) return;
            int typeCode = buf[4] >> 6;
            int vendorResets = (buf[4] >> 3) & 0x07;
            int userChanges = buf[4] & 0x07;
            int regionMask = buf[5];
            int scheme = buf[6];
            string[] types = { "região não definida", "região definida", "última troca disponível", "região permanente" };
            _log.Info("Estado: " + types[typeCode] + " | trocas restantes (usuário): " + userChanges + " | resets do fabricante: " + vendorResets);
            _log.Info("Região do drive (máscara 0x" + regionMask.ToString("X2") + "): " + RegionList(regionMask));
            _log.Info("Esquema RPC: " + (scheme == 1 ? "RPC Phase II" : scheme == 0 ? "desconhecido/RPC-1" : "código " + scheme));
            _summary.Add("Região do drive: " + RegionList(regionMask) + " (" + types[typeCode] + ")");
        }

        private async Task ReadTocAsync()
        {
            var buf = new byte[804];
            var r = await _drive.SendAsync(ScsiCommands.ReadToc(buf.Length), buf.Length, buf, 0, Timeout);
            _log.Info("Resultado: " + r.Describe());
            if (!r.Success || r.DataLength < 4) return;
            int len = Math.Min(r.DataLength, Bytes.BE16(buf, 0) + 2);
            _log.Info("Faixas " + buf[2] + " a " + buf[3]);
            for (int o = 4; o + 8 <= len; o += 8)
            {
                int track = buf[o + 2];
                bool data = (buf[o + 1] & 0x04) != 0;
                uint lba = Bytes.BE32(buf, o + 4);
                _log.Info("  Faixa " + (track == 0xAA ? "lead-out" : track.ToString()) + ": LBA " + lba + (data ? " (dados)" : " (áudio)"));
            }
            _log.Info("Obs.: no Marco 1 os CDs são lidos só com READ(10) (Modo 1). Modo 2 exige READ CD.");
        }

        private void CompareSizes()
        {
            _log.Section("Comparação de tamanhos");
            if (_capacity < 0 || _pfi0 == null)
            {
                _log.Info("Faltam dados (READ CAPACITY ou estrutura DVD) para comparar.");
                return;
            }
            long structure = _pfi0.TotalSectors;
            if (_pfi0.NumberOfLayers > 1 && !_pfi0.OppositeTrackPath && _pfi1 != null)
                structure = _pfi0.TotalSectors + _pfi1.TotalSectors; // PTP: soma as duas camadas
            _log.Info("READ CAPACITY: " + _capacity + " | estrutura DVD: " + structure + " | diferença: " + (_capacity - structure));
            string verdict = _capacity == structure ? "iguais" : "DIFERENTES (ver log)";
            _summary.Add("Tamanhos READ CAPACITY x estrutura: " + verdict);

            // No drive do Xbox (PLDS DG-6M5S), READ CAPACITY de um DVD-9 OTP veio como
            // "setor final - setor inicial + 1", ignorando a numeração complementada da camada 1, ou
            // seja, maior que o disco. A estrutura é a fonte confiável; as amostras usam ela.
            if (structure > 0 && structure != _capacity && (_pfi0.NumberOfLayers == 1 || _pfi0.OppositeTrackPath))
            {
                _log.Info("Usando o tamanho da estrutura DVD (" + structure + ") nas próximas etapas.");
                _capacity = structure;
            }
        }

        // ---------------------------------------------------------------- leituras

        private async Task<ScsiResult> ReadAsync(uint lba, int count, byte[] dest, int destOffset)
        {
            return await _drive.SendAsync(ScsiCommands.Read10(lba, count), count * SectorSize, dest, destOffset, Timeout);
        }

        /// <summary>
        /// Descobre quantos setores cabem num único READ(10) pelo pass-through. O XBSX2 usa no máximo 31
        /// (64 KiB menos o cabeçalho); queremos saber se 32 (64 KiB exatos) ou mais funcionam.
        /// Cada leitura usa um LBA diferente para não medir só o cache do drive. Também confere se uma
        /// leitura grande devolve os mesmos bytes que leituras menores do mesmo trecho.
        /// </summary>
        private async Task TransferSizeTestAsync()
        {
            var big = new byte[64 * SectorSize];
            var small = new byte[64 * SectorSize];
            var ok = new List<string>();
            uint lba = 2000;
            foreach (int n in TransferTests)
            {
                if (_capacity > 0 && lba + n > _capacity) break;
                var r = await ReadAsync(lba, n, big, 0);
                string line = n + " setores @ LBA " + lba + ": " + r.Describe();
                if (r.Success)
                {
                    double mbps = r.ElapsedMs > 0 ? (n * SectorSize / 1048576.0) / (r.ElapsedMs / 1000.0) : 0;
                    line += " ~" + mbps.ToString("0.0") + " MiB/s";
                    // Confere com leituras de 16 setores do mesmo trecho.
                    bool same = true;
                    for (int done = 0; done < n && same; done += SafeChunk)
                    {
                        int c = Math.Min(SafeChunk, n - done);
                        var rs = await ReadAsync(lba + (uint)done, c, small, done * SectorSize);
                        if (!rs.Success) { line += " | conferência falhou: " + rs.Describe(); same = false; break; }
                    }
                    if (same)
                    {
                        same = Bytes.Equal(big, 0, small, 0, n * SectorSize);
                        line += same ? " | conteúdo confere" : " | CONTEÚDO DIFERENTE das leituras de 16";
                    }
                    if (r.DataLength == n * SectorSize && same) ok.Add(n.ToString());
                }
                _log.Info(line);
                lba += 64;
            }
            _summary.Add("Setores por comando que funcionaram: " + (ok.Count > 0 ? string.Join(", ", ok) : "nenhum"));
        }

        /// <summary>
        /// Lê os primeiros setores em blocos de 16. Se um bloco falhar, lê setor a setor para isolar o
        /// problema. Setores que falharem ficam com zeros. Grava tudo em LocalState para análise no PC.
        /// </summary>
        private async Task<byte[]> ReadFirstSectorsAsync()
        {
            int total = FirstSectors;
            if (_capacity > 0 && _capacity < total) total = (int)_capacity;
            var data = new byte[total * SectorSize];
            var failures = new List<string>();
            int copyProtection = 0, mediumErrors = 0, otherErrors = 0;
            var sw = Stopwatch.StartNew();

            for (int lba = 0; lba < total; lba += SafeChunk)
            {
                int n = Math.Min(SafeChunk, total - lba);
                var r = await ReadAsync((uint)lba, n, data, lba * SectorSize);
                if (r.Success && r.DataLength == n * SectorSize) continue;

                _log.Info("Bloco LBA " + lba + "-" + (lba + n - 1) + " falhou: " + r.Describe() + " -> lendo setor a setor");
                Array.Clear(data, lba * SectorSize, n * SectorSize);
                for (int s = lba; s < lba + n; s++)
                {
                    var rs = await ReadAsync((uint)s, 1, data, s * SectorSize);
                    if (rs.Success && rs.DataLength == SectorSize) continue;
                    Array.Clear(data, s * SectorSize, SectorSize);
                    failures.Add("  LBA " + s + ": " + rs.Describe());
                    if (rs.Sense != null && rs.Sense.IsCopyProtection) copyProtection++;
                    else if (rs.Sense != null && rs.Sense.IsMediumError) mediumErrors++;
                    else otherErrors++;
                }
            }
            sw.Stop();

            double secs = sw.Elapsed.TotalSeconds;
            _log.Info("Lidos " + total + " setores em " + secs.ToString("0.00") + " s (~" +
                      (total * SectorSize / 1048576.0 / Math.Max(secs, 0.001)).ToString("0.0") + " MiB/s)");
            _log.Info("Setores com falha: " + failures.Count + " (proteção/CSS: " + copyProtection +
                      ", erro de mídia: " + mediumErrors + ", outros: " + otherErrors + ")");
            int shown = 0;
            foreach (var f in failures)
            {
                if (shown++ >= 50) { _log.Info("  ... (" + (failures.Count - 50) + " a mais)"); break; }
                _log.Info(f);
            }

            string path = Path.Combine(_folder, "spike-primeiros-" + total + "-setores.bin");
            File.WriteAllBytes(path, data);
            _log.Info("Gravado em " + path + " (setores com falha = zeros)");

            _summary.Add("Primeiros " + total + " setores: " + (total - failures.Count) + " OK, " + failures.Count + " com falha" +
                (copyProtection > 0 ? " (" + copyProtection + " recusados por proteção CSS)" : ""));
            return data;
        }

        private void AnalyzeFileSystem(byte[] data)
        {
            int sectors = data.Length / SectorSize;

            // Setores 0-15: "system area", em DVD-Video normalmente só zeros.
            _log.Info("Setores 0-15 (system area): " + (Bytes.AllZero(data, 0, Math.Min(16, sectors) * SectorSize) ? "todos zero" : "têm dados"));

            // Volume Recognition Sequence a partir do setor 16: ISO9660 "CD001", UDF "BEA01"/"NSR02"/"NSR03"/"TEA01".
            var ids = new List<string>();
            for (int s = 16; s < Math.Min(32, sectors); s++)
            {
                string id = Bytes.Ascii(data, s * SectorSize + 1, 5);
                if (id == "CD001" || id == "BEA01" || id == "NSR02" || id == "NSR03" || id == "TEA01" || id == "BOOT2" || id == "CDW02")
                    ids.Add(s + ":" + id + "(tipo " + data[s * SectorSize] + ")");
            }
            _log.Info("Identificadores de volume (setores 16-31): " + (ids.Count > 0 ? string.Join(" ", ids) : "nenhum"));

            bool iso = sectors > 16 && Bytes.Ascii(data, 16 * SectorSize + 1, 5) == "CD001" && data[16 * SectorSize] == 1;
            if (iso)
            {
                int o = 16 * SectorSize;
                string volumeId = Bytes.Ascii(data, o + 40, 32);
                uint volumeBlocks = Bytes.LE32(data, o + 80);
                _log.Info("ISO9660 PVD: volume \"" + volumeId + "\", " + volumeBlocks + " blocos de " + Bytes.LE16(data, o + 128) + " bytes");
                _summary.Add("ISO9660: \"" + volumeId + "\", " + volumeBlocks + " blocos" +
                    (_capacity > 0 ? (volumeBlocks == _capacity ? " (= capacidade)" : " (capacidade " + _capacity + ")") : ""));
            }

            // UDF: Anchor Volume Descriptor Pointer no setor 256, tag identifier 2 (little-endian).
            if (sectors > 256)
            {
                int o = 256 * SectorSize;
                ushort tag = Bytes.LE16(data, o);
                _log.Info("Setor 256: tag UDF = " + tag + (tag == 2 ? " (Anchor Volume Descriptor Pointer: UDF presente)" : ""));
                if (tag == 2) _summary.Add("UDF: AVDP encontrado no setor 256");
            }

            DumpSector(data, 0, 64);
            DumpSector(data, 16, 128);
            DumpSector(data, 17, 64);
            DumpSector(data, 256, 64);

            // Primeiro setor com cara de pacote MPEG (VOB) dentro dos primeiros setores, se houver.
            for (int s = 0; s < sectors; s++)
            {
                if (IsMpegPack(data, s * SectorSize))
                {
                    _log.Info("Primeiro pacote MPEG-PS (VOB) nos primeiros setores: LBA " + s +
                              (IsScrambledPack(data, s * SectorSize) ? " - marcado como EMBARALHADO (CSS)" : " - não embaralhado"));
                    DumpSector(data, s, 64);
                    break;
                }
            }
        }

        /// <summary>
        /// Lê trechos espalhados pelo disco. Os primeiros 1000 setores quase nunca têm vídeo; os VOBs
        /// (onde fica o CSS) ficam mais adiante. Isto responde as perguntas que só o console responde:
        /// o drive entrega setores de vídeo sem autenticação? Lê a camada 1 e a passagem entre camadas?
        /// </summary>
        private async Task SampleReadsAsync()
        {
            if (_capacity <= 0)
            {
                _log.Info("Sem capacidade conhecida; pulando.");
                return;
            }
            var points = new List<KeyValuePair<string, long>>();
            points.Add(new KeyValuePair<string, long>("10%", _capacity / 10));
            points.Add(new KeyValuePair<string, long>("25%", _capacity / 4));
            points.Add(new KeyValuePair<string, long>("50%", _capacity / 2));
            points.Add(new KeyValuePair<string, long>("75%", _capacity * 3 / 4));
            if (_pfi0 != null && _pfi0.NumberOfLayers > 1)
            {
                long l0 = _pfi0.Layer0Sectors;
                points.Add(new KeyValuePair<string, long>("fim da camada 0", l0 - SafeChunk));
                points.Add(new KeyValuePair<string, long>("início da camada 1", l0));
            }
            points.Add(new KeyValuePair<string, long>("fim do disco", _capacity - SafeChunk));

            var buf = new byte[SafeChunk * SectorSize];
            int okCount = 0, cssRefused = 0, otherFail = 0, scrambled = 0, packs = 0;
            foreach (var p in points)
            {
                long lba = Math.Max(0, Math.Min(p.Value, _capacity - SafeChunk));
                var r = await ReadAsync((uint)lba, SafeChunk, buf, 0);
                string line = p.Key + " (LBA " + lba + "-" + (lba + SafeChunk - 1) + "): " + r.Describe();
                if (r.Success)
                {
                    okCount++;
                    int pk = 0, sc = 0, zero = 0;
                    for (int s = 0; s < SafeChunk; s++)
                    {
                        if (Bytes.AllZero(buf, s * SectorSize, SectorSize)) zero++;
                        if (IsMpegPack(buf, s * SectorSize)) { pk++; if (IsScrambledPack(buf, s * SectorSize)) sc++; }
                    }
                    packs += pk; scrambled += sc;
                    line += " | pacotes MPEG: " + pk + ", embaralhados: " + sc + ", setores zerados: " + zero;
                }
                else if (r.Sense != null && r.Sense.IsCopyProtection) cssRefused++;
                else otherFail++;
                _log.Info(line);
                if (r.Success) foreach (var l in HexDump.Lines(buf, 0, 48, lba * SectorSize)) _log.Info("    " + l);
            }
            _summary.Add("Amostras: " + okCount + "/" + points.Count + " lidas; recusadas por CSS: " + cssRefused +
                         "; outras falhas: " + otherFail + "; pacotes MPEG " + packs + " (embaralhados " + scrambled + ")");
            if (cssRefused > 0)
                _summary.Add("=> O drive RECUSA setores protegidos sem autenticação CSS. Precisamos conversar antes do Marco 2.");
            else if (scrambled > 0)
                _summary.Add("=> O drive entrega os setores, mas EMBARALHADOS (CSS). A ISO precisará ser descriptografada no PC.");
        }

        // ---------------------------------------------------------------- utilitários

        /// <summary>Setor de DVD-Video: pack header MPEG-2 (00 00 01 BA).</summary>
        private static bool IsMpegPack(byte[] b, int o)
        {
            return b[o] == 0 && b[o + 1] == 0 && b[o + 2] == 1 && b[o + 3] == 0xBA;
        }

        /// <summary>
        /// Em VOBs, o byte 0x14 do setor contém os bits PES_scrambling_control (0x30) do primeiro
        /// pacote PES. Diferente de zero = conteúdo embaralhado por CSS (mesmo teste do libdvdcss).
        /// Só vale se o pacote PES em 0x0E for de vídeo (E0-EF), áudio MPEG (C0-DF) ou privado 1 (BD);
        /// em pacotes de navegação (system header 0xBB) o byte 0x14 é outro campo e nunca há CSS.
        /// </summary>
        private static bool IsScrambledPack(byte[] b, int o)
        {
            if (b[o + 0x0E] != 0 || b[o + 0x0F] != 0 || b[o + 0x10] != 1) return false;
            byte stream = b[o + 0x11];
            bool media = (stream >= 0xC0 && stream <= 0xEF) || stream == 0xBD;
            return media && (b[o + 0x14] & 0x30) != 0;
        }

        private void DumpSector(byte[] data, int sector, int bytes)
        {
            if ((sector + 1) * SectorSize > data.Length) return;
            _log.Info("Hexdump do setor " + sector + " (primeiros " + bytes + " bytes):");
            foreach (var l in HexDump.Lines(data, sector * SectorSize, bytes, (long)sector * SectorSize))
                _log.Info("  " + l);
        }

        /// <summary>RMI / máscara RPC: bit n LIGADO = região n+1 NÃO permitida.</summary>
        private static string RegionList(int mask)
        {
            var allowed = new List<string>();
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) == 0) allowed.Add((i + 1).ToString());
            if (allowed.Count == 8) return "todas";
            if (allowed.Count == 0) return "nenhuma";
            return string.Join(",", allowed);
        }

        private static string FormatBytes(long bytes)
        {
            return (bytes / 1073741824.0).ToString("0.00") + " GiB";
        }
    }
}
