using System;
using System.Globalization;
using System.IO;
using System.Text;
using Windows.Data.Json;

namespace xISOBurn.Core
{
    /// <summary>
    /// Configuração da cópia, guardada em LocalState\config.json. Se o arquivo não existir, é criado com
    /// os valores padrão. Para editar sem teclado no console, baixe o arquivo pelo Device Portal
    /// (File explorer > LocalAppData > xISOBurn_... > LocalState), edite no PC e envie de volta (Upload).
    /// O arquivo é relido a cada cópia.
    /// </summary>
    internal sealed class CopyConfig
    {
        public const string FileName = "config.json";

        /// <summary>Setores por READ(10). O spike mostrou que 16..64 funcionam no drive do Series X.</summary>
        public int SectorsPerRead = 32;
        /// <summary>Novas tentativas do bloco inteiro depois da primeira falha.</summary>
        public int BlockRetries = 5;
        /// <summary>Novas tentativas de cada setor, na leitura setor a setor.</summary>
        public int SectorRetries = 5;
        /// <summary>Tempo máximo de cada comando SCSI (segundos). Setor ruim pode levar vários segundos.</summary>
        public int TimeoutSeconds = 20;
        /// <summary>Primeiro setor a copiar (para testes de trechos).</summary>
        public long StartSector = 0;
        /// <summary>Quantidade máxima de setores (0 = até o fim do disco).</summary>
        public long SectorLimit = 0;
        /// <summary>false = só lê, calcula o hash e gera o mapa, sem gravar a imagem (teste do drive).</summary>
        public bool WriteImage = true;
        /// <summary>Nome do arquivo sem extensão. Vazio = rótulo do volume do disco.</summary>
        public string OutputName = "";

        public const int MaxSectorsPerRead = 64;

        public static CopyConfig Load(string folder, out string message)
        {
            var cfg = new CopyConfig();
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                cfg.Save(folder);
                message = "config.json criado com os valores padrão.";
                return cfg;
            }

            JsonObject o;
            if (!JsonObject.TryParse(File.ReadAllText(path), out o))
            {
                message = "config.json inválido (JSON mal formado); usando os valores padrão. O arquivo não foi alterado.";
                return cfg;
            }

            try
            {
                cfg.SectorsPerRead = (int)o.GetNamedNumber("SectorsPerRead", cfg.SectorsPerRead);
                cfg.BlockRetries = (int)o.GetNamedNumber("BlockRetries", cfg.BlockRetries);
                cfg.SectorRetries = (int)o.GetNamedNumber("SectorRetries", cfg.SectorRetries);
                cfg.TimeoutSeconds = (int)o.GetNamedNumber("TimeoutSeconds", cfg.TimeoutSeconds);
                cfg.StartSector = (long)o.GetNamedNumber("StartSector", cfg.StartSector);
                cfg.SectorLimit = (long)o.GetNamedNumber("SectorLimit", cfg.SectorLimit);
                cfg.WriteImage = o.GetNamedBoolean("WriteImage", cfg.WriteImage);
                cfg.OutputName = o.GetNamedString("OutputName", cfg.OutputName);
            }
            catch (Exception ex)
            {
                // GetNamed* lança exceção se o tipo do valor estiver errado (ex.: número entre aspas).
                message = "config.json com valor de tipo errado (" + ex.Message + "); usando os valores padrão.";
                return new CopyConfig();
            }
            message = cfg.Clamp() ? "config.json lido (alguns valores fora do limite foram ajustados)." : "config.json lido.";
            return cfg;
        }

        /// <summary>Mantém os valores em faixas seguras. Devolve true se algo foi ajustado.</summary>
        private bool Clamp()
        {
            bool changed = false;
            int spr = Math.Max(1, Math.Min(MaxSectorsPerRead, SectorsPerRead));
            int br = Math.Max(0, Math.Min(50, BlockRetries));
            int sr = Math.Max(0, Math.Min(50, SectorRetries));
            int to = Math.Max(5, Math.Min(120, TimeoutSeconds));
            long ss = Math.Max(0, StartSector);
            long sl = Math.Max(0, SectorLimit);
            changed = spr != SectorsPerRead || br != BlockRetries || sr != SectorRetries || to != TimeoutSeconds || ss != StartSector || sl != SectorLimit;
            SectorsPerRead = spr; BlockRetries = br; SectorRetries = sr; TimeoutSeconds = to; StartSector = ss; SectorLimit = sl;
            return changed;
        }

        public void Save(string folder)
        {
            // Escrito à mão para ficar legível e com ordem fixa.
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"SectorsPerRead\": ").Append(SectorsPerRead).Append(",\n");
            sb.Append("  \"BlockRetries\": ").Append(BlockRetries).Append(",\n");
            sb.Append("  \"SectorRetries\": ").Append(SectorRetries).Append(",\n");
            sb.Append("  \"TimeoutSeconds\": ").Append(TimeoutSeconds).Append(",\n");
            sb.Append("  \"StartSector\": ").Append(StartSector.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"SectorLimit\": ").Append(SectorLimit.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"WriteImage\": ").Append(WriteImage ? "true" : "false").Append(",\n");
            sb.Append("  \"OutputName\": ").Append(JsonValue.CreateStringValue(OutputName ?? "").Stringify()).Append("\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(folder, FileName), sb.ToString(), new UTF8Encoding(false));
        }

        public string Describe()
        {
            return "bloco " + SectorsPerRead + " setores | tentativas: bloco " + BlockRetries + ", setor " + SectorRetries +
                   " | timeout " + TimeoutSeconds + " s | início " + StartSector +
                   " | limite " + (SectorLimit > 0 ? SectorLimit.ToString() : "fim do disco") +
                   " | gravar imagem: " + (WriteImage ? "sim" : "não");
        }
    }
}
