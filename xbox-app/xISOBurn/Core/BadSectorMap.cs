using System.Collections.Generic;
using System.Text;

namespace xISOBurn.Core
{
    /// <summary>
    /// Intervalos de setores que não puderam ser lidos e foram preenchidos com zeros.
    /// Os setores chegam em ordem crescente, então intervalos contíguos são unidos na hora.
    /// </summary>
    internal sealed class BadSectorMap
    {
        public sealed class Range
        {
            public long Start;
            public long End; // inclusivo
            public string LastError;
            public long Count { get { return End - Start + 1; } }
        }

        private readonly List<Range> _ranges = new List<Range>();

        public long TotalSectors { get; private set; }
        public IReadOnlyList<Range> Ranges { get { return _ranges; } }

        public void Add(long lba, string error)
        {
            TotalSectors++;
            if (_ranges.Count > 0)
            {
                var last = _ranges[_ranges.Count - 1];
                if (last.End + 1 == lba)
                {
                    last.End = lba;
                    last.LastError = error;
                    return;
                }
            }
            _ranges.Add(new Range { Start = lba, End = lba, LastError = error });
        }

        /// <summary>
        /// Arquivo .map em texto. Linhas com '#' são comentários; cada linha de dados é
        /// "início-fim (quantidade) último erro", com LBAs de 2048 bytes, intervalo inclusivo.
        /// </summary>
        public string ToText(string volume, long firstSector, long endSector, long sectorsDone, bool complete, string sha256)
        {
            var sb = new StringBuilder();
            sb.Append("# xISOBurn - mapa de setores\n");
            sb.Append("# volume: ").Append(volume).Append('\n');
            sb.Append("# setor = 2048 bytes; intervalos inclusivos; LBA absoluto no disco\n");
            sb.Append("# trecho pedido: ").Append(firstSector).Append('-').Append(endSector - 1).Append('\n');
            sb.Append("# trecho processado: ").Append(firstSector).Append('-').Append(firstSector + sectorsDone - 1)
              .Append(complete ? " (completo)" : " (INTERROMPIDO)").Append('\n');
            sb.Append("# setores lidos com sucesso: ").Append(sectorsDone - TotalSectors).Append('\n');
            sb.Append("# setores NÃO lidos (preenchidos com zeros na imagem): ").Append(TotalSectors)
              .Append(" em ").Append(_ranges.Count).Append(" intervalo(s)\n");
            if (!string.IsNullOrEmpty(sha256))
                sb.Append("# sha256 da imagem (incluindo os zeros): ").Append(sha256).Append('\n');
            sb.Append("# início-fim (quantidade) último erro\n");
            foreach (var r in _ranges)
                sb.Append(r.Start).Append('-').Append(r.End).Append(" (").Append(r.Count).Append(") ").Append(r.LastError).Append('\n');
            return sb.ToString();
        }
    }
}
