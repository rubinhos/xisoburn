using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>Sem disco no drive (02/3A): a cópia para; não é um setor ruim.</summary>
    internal sealed class MediumRemovedException : Exception
    {
        public MediumRemovedException(string message) : base(message) { }
    }

    internal sealed class FailedSector
    {
        public long Lba;
        public string Error;
    }

    /// <summary>
    /// Leitura de um bloco com recuperação de erros:
    ///   1. lê o bloco inteiro; se falhar, tenta de novo BlockRetries vezes;
    ///   2. se continuar falhando, lê setor a setor, com 1 + SectorRetries tentativas cada;
    ///   3. setor que não ler fica com zeros no buffer e vai para a lista devolvida (mapa de erros).
    /// Nunca lança exceção por setor ruim. Só para se o disco sair do drive ou se a cópia for cancelada.
    /// </summary>
    internal sealed class SectorReader
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private static readonly List<FailedSector> NoFailures = new List<FailedSector>();

        private readonly OpticalDrive _drive;
        private readonly CopyConfig _cfg;
        private readonly Logger _log;

        public long Commands { get; private set; }
        public long RecoveredBlocks { get; private set; }
        public long RecoveredSectors { get; private set; }

        public SectorReader(OpticalDrive drive, CopyConfig cfg, Logger log)
        {
            _drive = drive;
            _cfg = cfg;
            _log = log;
        }

        public async Task<List<FailedSector>> ReadAsync(long lba, int count, byte[] buffer, CancellationToken ct)
        {
            uint timeout = (uint)_cfg.TimeoutSeconds;
            int blockAttempts = 1 + _cfg.BlockRetries;
            string range = lba + "-" + (lba + count - 1);

            for (int attempt = 1; attempt <= blockAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Commands++;
                var r = await _drive.SendAsync(ScsiCommands.Read10((uint)lba, count), count * SectorSize, buffer, 0, timeout);
                if (r.Success && r.DataLength == count * SectorSize)
                {
                    if (attempt > 1)
                    {
                        RecoveredBlocks++;
                        _log.Info("Bloco " + range + " lido na tentativa " + attempt + ".");
                    }
                    return NoFailures;
                }
                ThrowIfFatal(r);
                _log.Info("Bloco " + range + ": tentativa " + attempt + "/" + blockAttempts + " falhou: " + r.Describe());
            }

            _log.Info("Bloco " + range + ": lendo setor a setor.");
            var failed = new List<FailedSector>();
            int sectorAttempts = 1 + _cfg.SectorRetries;
            for (int i = 0; i < count; i++)
            {
                long s = lba + i;
                ScsiResult last = null;
                bool ok = false;
                for (int attempt = 1; attempt <= sectorAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    Commands++;
                    last = await _drive.SendAsync(ScsiCommands.Read10((uint)s, 1), SectorSize, buffer, i * SectorSize, timeout);
                    if (last.Success && last.DataLength == SectorSize)
                    {
                        ok = true;
                        if (attempt > 1)
                        {
                            RecoveredSectors++;
                            _log.Info("Setor " + s + " lido na tentativa " + attempt + ".");
                        }
                        break;
                    }
                    ThrowIfFatal(last);
                }
                if (!ok)
                {
                    // Zeros no lugar do setor: a imagem mantém o tamanho e os offsets certos.
                    Array.Clear(buffer, i * SectorSize, SectorSize);
                    string error = last.Sense != null ? last.Sense.Code + " " + last.Sense.Describe() : last.Describe();
                    failed.Add(new FailedSector { Lba = s, Error = error });
                    _log.Info("Setor " + s + " ILEGÍVEL após " + sectorAttempts + " tentativas: " + last.Describe() + " -> zeros");
                }
            }
            return failed;
        }

        private static void ThrowIfFatal(ScsiResult r)
        {
            if (r.Sense != null && r.Sense.IsNoMedium)
                throw new MediumRemovedException("Disco removido ou drive sem mídia: " + r.Sense);
        }
    }
}
