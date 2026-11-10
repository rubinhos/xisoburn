// SPDX-License-Identifier: GPL-2.0-or-later
// The keys folder on the Xbox, filled from the web page:
//   keys/KEYDB.cfg, ProcessingDeviceKeysSimple.txt, HostKeyCertificate.txt   (AACS, libaacs names)
//   keys/bdplus/vm0/...        BD+ player files (memory.map, aes_keys.bin, ecdsa_keys.txt, ...)
//   keys/bdplus/convtab/*.bin  cached BD+ conversion tables, named after the media key
// A .zip is unpacked with the same rules (a "vm0/" or "convtab/" folder inside it keeps its place).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace xISOBurn.Bluray
{
    internal static class KeyStore
    {
        private static readonly string[] AacsNames = { Aacs.KeyDb.KeyDbFile, Aacs.KeyDb.PkFile, Aacs.KeyDb.CertFile };

        private static bool IsConvtabName(string name)
        {
            if (name.Length < 36 || !name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 0; i < 32; i++) if (!Big.IsHexDigit(name[i])) return false;
            return true;
        }

        /// <summary>Relative destination of a file by its name (and its path inside a zip); null = skip.</summary>
        private static string Destination(string entryPath, bool fromZip)
        {
            string p = entryPath.Replace('\\', '/');
            string name = p.Substring(p.LastIndexOf('/') + 1);
            if (name.Length == 0 || name.StartsWith(".")) return null;
            int vm0 = p.IndexOf("vm0/", StringComparison.OrdinalIgnoreCase);
            if (vm0 >= 0) return "bdplus/vm0/" + p.Substring(vm0 + 4);
            int ct = p.IndexOf("convtab/", StringComparison.OrdinalIgnoreCase);
            if (ct >= 0 || IsConvtabName(name)) return "bdplus/convtab/" + name;
            foreach (var a in AacsNames)
                if (string.Equals(name, a, StringComparison.OrdinalIgnoreCase)) return a;
            if (fromZip) return null;
            // Any other single file is taken as a BD+ player file (memory.map names its own files).
            return "bdplus/vm0/" + name;
        }

        private static string Safe(string folder, string rel)
        {
            string full = Path.GetFullPath(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("invalid path " + rel);
            return full;
        }

        /// <summary>Installs an uploaded file (already saved at 'tempPath'). Returns what was done.</summary>
        public static string Install(string keysFolder, string fileName, string tempPath)
        {
            Directory.CreateDirectory(keysFolder);
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var done = new List<string>();
                int skipped = 0;
                using (var fs = File.OpenRead(tempPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        string rel = Destination(e.FullName, true);
                        if (rel == null) { skipped++; continue; }
                        string dest = Safe(keysFolder, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var src = e.Open())
                        using (var dst = File.Create(dest))
                            src.CopyTo(dst);
                        done.Add(rel);
                    }
                }
                File.Delete(tempPath);
                return fileName + ": " + done.Count + " file(s) installed" + (skipped > 0 ? ", " + skipped + " skipped (unknown names)" : "") +
                       (done.Count > 0 && done.Count <= 12 ? " (" + string.Join(", ", done) + ")" : "") + ".";
            }
            string r = Destination(fileName, false);
            string target = Safe(keysFolder, r);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target)) File.Delete(target);
            File.Move(tempPath, target);
            return fileName + " installed as " + r + ".";
        }

        /// <summary>Installed files: relative path and size.</summary>
        public static List<KeyValuePair<string, long>> List(string keysFolder)
        {
            var list = new List<KeyValuePair<string, long>>();
            if (!Directory.Exists(keysFolder)) return list;
            string root = Path.GetFullPath(keysFolder) + Path.DirectorySeparatorChar;
            foreach (var f in Directory.GetFiles(keysFolder, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetFullPath(f).Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.EndsWith(".upload")) continue;
                list.Add(new KeyValuePair<string, long>(rel, new FileInfo(f).Length));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        public static void DeleteAll(string keysFolder)
        {
            if (Directory.Exists(keysFolder)) Directory.Delete(keysFolder, true);
        }
    }
}
