using xISOBurn.Util;

namespace xISOBurn.Scsi
{
    /// <summary>
    /// Montagem dos CDBs (Command Descriptor Blocks). Todos os campos multibyte são big-endian.
    /// Referências: SPC-4 (INQUIRY, TEST UNIT READY), SBC/MMC-6 (READ CAPACITY, READ(10),
    /// GET CONFIGURATION, READ DVD STRUCTURE, REPORT KEY, READ TOC).
    /// </summary>
    internal static class ScsiCommands
    {
        /// <summary>TEST UNIT READY (0x00): sem dados; GOOD = disco pronto.</summary>
        public static byte[] TestUnitReady()
        {
            return new byte[6];
        }

        /// <summary>REQUEST SENSE (0x03): pede o sense do último erro, quando o driver não o devolveu.</summary>
        public static byte[] RequestSense(byte allocationLength)
        {
            return new byte[] { 0x03, 0, 0, 0, allocationLength, 0 };
        }

        /// <summary>INQUIRY (0x12): fabricante, modelo e firmware do drive.</summary>
        public static byte[] Inquiry(byte allocationLength)
        {
            return new byte[] { 0x12, 0, 0, 0, allocationLength, 0 };
        }

        /// <summary>
        /// READ CAPACITY(10) (0x25): devolve o ÚLTIMO LBA (não a contagem) e o tamanho do bloco.
        /// Setores = último LBA + 1. Em DVD dupla camada OTP o LBA é contínuo entre as camadas,
        /// então o valor já cobre as duas (a conferir no spike contra READ DVD STRUCTURE).
        /// </summary>
        public static byte[] ReadCapacity10()
        {
            return new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        }

        /// <summary>
        /// GET CONFIGURATION (0x46), RT=0, a partir da feature 0. Os bytes 6-7 do cabeçalho trazem o
        /// "perfil atual", que identifica a mídia (CD-ROM, DVD-ROM, BD-ROM...).
        /// </summary>
        public static byte[] GetConfiguration(int allocationLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x46;
            Bytes.PutBE16(cdb, 7, allocationLength);
            return cdb;
        }

        /// <summary>
        /// READ(10) (0x28): lê 'count' setores de 2048 bytes a partir de 'lba'.
        /// Mesmo CDB do XBSX2 (DPO/FUA = 0: o drive pode usar o cache dele).
        /// </summary>
        public static byte[] Read10(uint lba, int count)
        {
            var cdb = new byte[10];
            cdb[0] = 0x28;
            Bytes.PutBE32(cdb, 2, lba);
            Bytes.PutBE16(cdb, 7, count);
            return cdb;
        }

        /// <summary>
        /// READ DVD STRUCTURE (0xAD), mídia tipo 0 (DVD).
        ///   formato 0x00 = Physical Format Information (tipo de livro, camadas, setores);
        ///   formato 0x01 = Copyright Information (sistema de proteção e região do disco).
        /// O formato 0x02 (Disc Key) exige autenticação CSS e NÃO é usado.
        /// </summary>
        public static byte[] ReadDvdStructure(byte layer, byte format, int allocationLength)
        {
            var cdb = new byte[12];
            cdb[0] = 0xAD;
            cdb[6] = layer;
            cdb[7] = format;
            Bytes.PutBE16(cdb, 8, allocationLength);
            return cdb;
        }

        /// <summary>
        /// REPORT KEY (0xA4) com KEY FORMAT 0x08 = "RPC State": só informa a região configurada no
        /// drive e quantas trocas restam. Não participa de autenticação CSS e não altera nada.
        /// </summary>
        public static byte[] ReportKeyRpcState()
        {
            var cdb = new byte[12];
            cdb[0] = 0xA4;
            Bytes.PutBE16(cdb, 8, 8); // allocation length
            cdb[10] = 0x08;            // AGID 0 (bits 7-6) | KEY FORMAT 0x08
            return cdb;
        }

        /// <summary>READ TOC/PMA/ATIP (0x43), formato 0 (TOC), endereços em LBA. Só para CDs.</summary>
        public static byte[] ReadToc(int allocationLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x43;
            Bytes.PutBE16(cdb, 7, allocationLength);
            return cdb;
        }
    }
}
