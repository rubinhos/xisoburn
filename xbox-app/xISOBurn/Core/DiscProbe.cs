using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// Detecta o disco e decide quantos setores copiar.
    ///
    /// Decisão de tamanho (descoberta do spike, docs/spike.md): em DVD dupla camada OTP, o READ CAPACITY
    /// do drive do Xbox devolveu "setor físico final - inicial + 1" (15.939.015 em vez de 3.317.799).
    /// Por isso, em DVD, a fonte principal é READ DVD STRUCTURE (cálculo OTP), conferida com o tamanho
    /// do volume ISO9660. READ CAPACITY só é usado em CD/BD ou se a estrutura falhar.
    /// </summary>
    internal sealed class DiscProbe
    {
        private const int SectorSize = OpticalDrive.SectorSize;

        public bool Ready;
        public string NotReadyReason = "";
        public int Profile = -1;
        public long ReadCapacitySectors = -1;
        public DvdPhysicalFormat Pfi;
        public int Cpst = -1;
        public string VolumeLabel = "";
        public long Iso9660Sectors = -1;
        public bool Udf;
        public long TotalSectors = -1;
        public string TotalSource = "";
        public readonly List<string> Warnings = new List<string>();

        public static async Task<DiscProbe> RunAsync(OpticalDrive drive, uint timeout, Logger log)
        {
            var p = new DiscProbe();
            log.Section("Detectando o disco");

            // TEST UNIT READY, esperando o disco acelerar se preciso (até ~20 s).
            for (int attempt = 1; attempt <= 20; attempt++)
            {
                var r = await drive.SendAsync(ScsiCommands.TestUnitReady(), 0, null, 0, timeout);
                if (r.Success) { p.Ready = true; break; }
                p.NotReadyReason = r.Describe();
                bool transient = r.Sense != null && ((r.Sense.Key == 0x02 && r.Sense.Asc == 0x04) || r.Sense.Key == 0x06);
                if (!transient) break;
                await Task.Delay(1000);
            }
            if (!p.Ready)
            {
                log.Info("Drive não está pronto: " + p.NotReadyReason);
                return p;
            }

            var buf = new byte[36];
            var rc = await drive.SendAsync(ScsiCommands.GetConfiguration(8), 8, buf, 0, timeout);
            if (rc.Success && rc.DataLength >= 8) p.Profile = Bytes.BE16(buf, 6);
            log.Info("Perfil: " + (p.Profile >= 0 ? "0x" + p.Profile.ToString("X4") + " " + MmcProfiles.Name(p.Profile) : rc.Describe()));

            rc = await drive.SendAsync(ScsiCommands.ReadCapacity10(), 8, buf, 0, timeout);
            if (rc.Success && rc.DataLength >= 8) p.ReadCapacitySectors = (long)Bytes.BE32(buf, 0) + 1;
            log.Info("READ CAPACITY: " + (p.ReadCapacitySectors >= 0 ? p.ReadCapacitySectors + " setores" : rc.Describe()));

            bool dvd = MmcProfiles.IsDvd(p.Profile);
            if (dvd || p.Profile < 0)
            {
                rc = await drive.SendAsync(ScsiCommands.ReadDvdStructure(0, 0x00, 36), 36, buf, 0, timeout);
                if (rc.Success) p.Pfi = DvdPhysicalFormat.Parse(buf, rc.DataLength);
                if (p.Pfi != null)
                    log.Info("Estrutura DVD: " + p.Pfi.BookTypeName + ", " + p.Pfi.NumberOfLayers + " camada(s)" +
                             (p.Pfi.NumberOfLayers > 1 ? (p.Pfi.OppositeTrackPath ? " OTP" : " PTP") : "") +
                             ", " + p.Pfi.TotalSectors + " setores, camada 0 = " + p.Pfi.Layer0Sectors);
                else
                    log.Info("Estrutura DVD: " + rc.Describe());

                var cb = new byte[8];
                rc = await drive.SendAsync(ScsiCommands.ReadDvdStructure(0, 0x01, 8), 8, cb, 0, timeout);
                if (rc.Success && rc.DataLength >= 6) p.Cpst = cb[4];
                log.Info("Proteção (CPST): " + (p.Cpst >= 0 ? CpstName(p.Cpst) : rc.Describe()));
            }

            // Sistema de arquivos: PVD ISO9660 no setor 16 e âncora UDF no setor 256.
            var sec = new byte[SectorSize];
            rc = await drive.SendAsync(ScsiCommands.Read10(16, 1), SectorSize, sec, 0, timeout);
            if (rc.Success && sec[0] == 1 && Bytes.Ascii(sec, 1, 5) == "CD001")
            {
                p.VolumeLabel = Bytes.Ascii(sec, 40, 32);
                p.Iso9660Sectors = Bytes.LE32(sec, 80);
                log.Info("ISO9660: \"" + p.VolumeLabel + "\", " + p.Iso9660Sectors + " blocos");
            }
            rc = await drive.SendAsync(ScsiCommands.Read10(256, 1), SectorSize, sec, 0, timeout);
            if (rc.Success && Bytes.LE16(sec, 0) == 2)
            {
                p.Udf = true;
                log.Info("UDF: âncora (AVDP) no setor 256");
            }

            p.DecideSize();
            log.Info("Tamanho a copiar: " + p.TotalSectors + " setores (" + FormatSize(p.TotalSectors) + "), fonte: " + p.TotalSource);
            foreach (var w in p.Warnings) log.Info("AVISO: " + w);
            return p;
        }

        private void DecideSize()
        {
            long structure = -1;
            if (Pfi != null)
            {
                if (Pfi.NumberOfLayers == 1 || Pfi.OppositeTrackPath) structure = Pfi.TotalSectors;
                else Warnings.Add("DVD dupla camada PTP: o tamanho da estrutura não cobre a camada 1; usando READ CAPACITY.");
            }

            if (structure > 0)
            {
                TotalSectors = structure;
                TotalSource = "estrutura do DVD";
                if (ReadCapacitySectors > 0 && ReadCapacitySectors != structure)
                    Warnings.Add("READ CAPACITY (" + ReadCapacitySectors + ") difere da estrutura (" + structure +
                                 "). Esperado em DVD-9 OTP neste drive; vale a estrutura.");
            }
            else if (ReadCapacitySectors > 0)
            {
                TotalSectors = ReadCapacitySectors;
                TotalSource = "READ CAPACITY";
            }
            else if (Iso9660Sectors > 0)
            {
                TotalSectors = Iso9660Sectors;
                TotalSource = "volume ISO9660";
            }

            if (Iso9660Sectors > 0 && TotalSectors > 0 && Iso9660Sectors != TotalSectors)
            {
                Warnings.Add("O volume ISO9660 diz " + Iso9660Sectors + " setores e o disco " + TotalSectors + ".");
                if (Iso9660Sectors > TotalSectors)
                {
                    // Melhor copiar a mais (o drive recusa além do fim e isso fica no mapa) do que cortar dados.
                    TotalSectors = Iso9660Sectors;
                    TotalSource += " (ampliado para o tamanho do ISO9660)";
                }
            }
        }

        public IEnumerable<string> Describe()
        {
            if (!Ready)
            {
                yield return "Sem disco pronto: " + NotReadyReason;
                yield break;
            }
            string media = Profile >= 0 ? MmcProfiles.Name(Profile) : "mídia desconhecida";
            if (Pfi != null && Pfi.NumberOfLayers > 1)
                media += ", 2 camadas (" + (Pfi.OppositeTrackPath ? "OTP" : "PTP") + ")";
            else if (Pfi != null)
                media += ", 1 camada";
            yield return media;
            yield return "Volume: " + (VolumeLabel.Length > 0 ? VolumeLabel : "(sem ISO9660)") + (Udf ? " | UDF" : "");
            yield return "Tamanho: " + TotalSectors.ToString("N0") + " setores (" + FormatSize(TotalSectors) + ") - " + TotalSource;
            if (Pfi != null && Pfi.NumberOfLayers > 1)
                yield return "Troca de camada no LBA " + Pfi.Layer0Sectors.ToString("N0");
            if (Cpst >= 0)
                yield return "Proteção: " + CpstName(Cpst) + (Cpst == 1 ? " (a imagem pode sair embaralhada)" : "");
            foreach (var w in Warnings) yield return "Aviso: " + w;
        }

        private static string CpstName(int cpst)
        {
            switch (cpst)
            {
                case 0: return "nenhuma";
                case 1: return "CSS/CPPM";
                case 2: return "CPRM";
                default: return "código 0x" + cpst.ToString("X2");
            }
        }

        public static string FormatSize(long sectors)
        {
            if (sectors < 0) return "?";
            return (sectors * (double)SectorSize / 1073741824.0).ToString("0.00") + " GiB";
        }
    }
}
