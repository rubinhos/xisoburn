// SPDX-License-Identifier: LGPL-2.1-or-later
// KEYDB.cfg reader: same grammar as libaacs src/file/keydbcfg-parser.y and keydbcfg-lexer.l, plus the
// simple ProcessingDeviceKeysSimple.txt / HostKeyCertificate.txt files of keydbcfg.c.
// Copyright (C) 2010-2013 npzacs, LGPL-2.1-or-later.
//
// Written by hand instead of flex/bison. Only the entry of the disc being copied is kept: a full
// KEYDB.cfg (one line per known disc) can be tens of megabytes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace xISOBurn.Bluray.Aacs
{
    internal sealed class DeviceKey
    {
        public byte[] Key;          // 16
        public uint Node;
        public uint Uv;
        public int UMaskShift;
    }

    internal sealed class HostCert
    {
        public byte[] PrivKey;      // 20
        public byte[] Cert;         // 92
    }

    /// <summary>Per-disc entry: "0x[disc id] = title | D | date | M | mk | I | vid | V | vuk | U | 1-0x.. | 2-0x.."</summary>
    internal sealed class DiscEntry
    {
        public byte[] DiscId;       // 20
        public string Title = "";
        public byte[] Mk, Vid, Vuk; // 16 each, null if absent
        public List<byte[]> UnitKeys; // in file order (1-.., 2-..), null if absent
    }

    internal sealed class KeyDb
    {
        public readonly List<DeviceKey> DeviceKeys = new List<DeviceKey>();
        public readonly List<byte[]> ProcessingKeys = new List<byte[]>();
        public readonly List<HostCert> HostCerts = new List<HostCert>();
        public DiscEntry Disc;
        public readonly List<string> Notes = new List<string>();
        public int BadLines;

        public bool Empty
        {
            get { return DeviceKeys.Count == 0 && ProcessingKeys.Count == 0 && HostCerts.Count == 0 && Disc == null; }
        }

        public const string KeyDbFile = "KEYDB.cfg";
        public const string PkFile = "ProcessingDeviceKeysSimple.txt";
        public const string CertFile = "HostKeyCertificate.txt";

        /// <summary>Loads the key files found in 'folder', keeping only the entry for 'discId'.</summary>
        public static KeyDb Load(string folder, byte[] discId)
        {
            var db = new KeyDb();
            string path = Path.Combine(folder, KeyDbFile);
            if (File.Exists(path))
            {
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                    db.Parse(reader, discId);
                db.Notes.Add(KeyDbFile + ": " + db.DeviceKeys.Count + " device key(s), " + db.ProcessingKeys.Count +
                             " processing key(s), " + db.HostCerts.Count + " host certificate(s)" +
                             (db.Disc != null ? ", entry for this disc: \"" + db.Disc.Title.Trim() + "\"" : ", no entry for this disc") +
                             (db.BadLines > 0 ? ", " + db.BadLines + " line(s) not understood" : "") + ".");
            }
            path = Path.Combine(folder, PkFile);
            if (File.Exists(path)) db.ParsePkFile(File.ReadAllText(path));
            path = Path.Combine(folder, CertFile);
            if (File.Exists(path)) db.ParseCertFile(File.ReadAllText(path));
            return db;
        }

        // ------------------------------------------------------------ KEYDB.cfg

        public void Parse(TextReader reader, byte[] discId)
        {
            string want = discId != null ? Big.Hex(discId) : null;
            var line = new StringBuilder();
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                // NEWLINE_ESCAPE: a backslash at the end of a line joins it with the next one; the
                // escaped newline counts as whitespace.
                if (raw.EndsWith("\\"))
                {
                    line.Append(raw, 0, raw.Length - 1).Append(' ');
                    continue;
                }
                line.Append(raw);
                string l = line.ToString();
                line.Clear();
                ParseLine(l, want);
            }
            if (line.Length > 0) ParseLine(line.ToString(), want);
        }

        private void ParseLine(string l, string wantDiscId)
        {
            int i = 0;
            while (i < l.Length && (l[i] == ' ' || l[i] == '\t')) i++;
            if (i >= l.Length || l[i] == ';') return;

            // Title entries are most of a big file: skip the other discs without tokenizing.
            if (i + 2 < l.Length && l[i] == '0' && (l[i + 1] == 'x' || l[i + 1] == 'X'))
            {
                if (wantDiscId == null) return;
                if (i + 42 > l.Length || string.Compare(l, i + 2, wantDiscId, 0, 40, StringComparison.OrdinalIgnoreCase) != 0)
                    return;
            }

            List<Token> t;
            try { t = Tokenize(l); }
            catch (FormatException) { BadLines++; return; }
            if (t.Count == 0) return;
            bool ok;
            try { ok = ParseTokens(t); }
            catch (Exception) { ok = false; }
            if (!ok) BadLines++;
        }

        private enum T { EntryId, Bar, Equals, Hyphen, Hex, Digit, Keyword, Title }

        private struct Token
        {
            public T Type;
            public string Text;
            public Token(T type, string text) { Type = type; Text = text; }
        }

        private static readonly string[] Keywords =
        {
            "DEVICE_KEY", "DEVICE_NODE", "KEY_UV", "KEY_U_MASK_SHIFT",
            "HOST_PRIV_KEY", "HOST_CERT", "HOST_NONCE", "HOST_KEY_POINT"
        };

        private static List<Token> Tokenize(string l)
        {
            var list = new List<Token>();
            int i = 0;
            while (i < l.Length)
            {
                char c = l[i];
                if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }
                if (c == ';') break; // COMMENT
                if (c == '|')
                {
                    // ENTRY_ID: "|" spaces letters spaces "|"
                    int j = i + 1;
                    while (j < l.Length && (l[j] == ' ' || l[j] == '\t')) j++;
                    int s = j;
                    while (j < l.Length && char.IsLetter(l[j])) j++;
                    int e = j;
                    while (j < l.Length && (l[j] == ' ' || l[j] == '\t')) j++;
                    if (e > s && e - s <= 2 && j < l.Length && l[j] == '|')
                    {
                        list.Add(new Token(T.EntryId, l.Substring(s, e - s).ToUpperInvariant()));
                        i = j + 1;
                    }
                    else
                    {
                        list.Add(new Token(T.Bar, "|"));
                        i++;
                    }
                    continue;
                }
                if (c == '=')
                {
                    // PUNCT_EQUALS_SIGN, then DISC_TITLE: anything up to '|', '\' or the end of the line.
                    list.Add(new Token(T.Equals, "="));
                    int j = i + 1;
                    while (j < l.Length && l[j] != '|' && l[j] != '\\') j++;
                    list.Add(new Token(T.Title, l.Substring(i + 1, j - i - 1)));
                    i = j;
                    continue;
                }
                if (c == '-') { list.Add(new Token(T.Hyphen, "-")); i++; continue; }
                if (c == '0' && i + 2 < l.Length && (l[i + 1] == 'x' || l[i + 1] == 'X') && Big.IsHexDigit(l[i + 2]))
                {
                    int j = i + 2;
                    while (j < l.Length && Big.IsHexDigit(l[j])) j++;
                    list.Add(new Token(T.Hex, l.Substring(i + 2, j - i - 2)));
                    i = j;
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int j = i;
                    while (j < l.Length && char.IsDigit(l[j])) j++;
                    list.Add(new Token(T.Digit, l.Substring(i, j - i)));
                    i = j;
                    continue;
                }
                if (char.IsLetter(c))
                {
                    int j = i;
                    while (j < l.Length && (char.IsLetter(l[j]) || l[j] == '_')) j++;
                    string word = l.Substring(i, j - i).ToUpperInvariant();
                    if (Array.IndexOf(Keywords, word) < 0) throw new FormatException("unknown word " + word);
                    list.Add(new Token(T.Keyword, word));
                    i = j;
                    continue;
                }
                throw new FormatException("unexpected character");
            }
            return list;
        }

        /// <summary>Concatenated hexstring_list starting at t[i]; advances i.</summary>
        private static string HexList(List<Token> t, ref int i)
        {
            var sb = new StringBuilder();
            while (i < t.Count && t[i].Type == T.Hex) sb.Append(t[i++].Text);
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string Keyword(List<Token> t, ref int i, string name)
        {
            if (i >= t.Count || t[i].Type != T.Keyword || t[i].Text != name) return null;
            i++;
            return HexList(t, ref i);
        }

        private static bool Expect(List<Token> t, ref int i, T type)
        {
            if (i < t.Count && t[i].Type == type) { i++; return true; }
            return false;
        }

        private static byte[] Key(string hex, int bytes)
        {
            if (hex == null || hex.Length != bytes * 2) return null;
            return Big.HexToBytes(hex);
        }

        private bool ParseTokens(List<Token> t)
        {
            int i = 0;
            if (t[0].Type == T.EntryId && t[0].Text == "DK")
            {
                i = 1;
                string key = Keyword(t, ref i, "DEVICE_KEY");
                if (!Expect(t, ref i, T.Bar)) return false;
                string node = Keyword(t, ref i, "DEVICE_NODE");
                string uv = null, shift = null;
                if (Expect(t, ref i, T.Bar))
                {
                    uv = Keyword(t, ref i, "KEY_UV");
                    if (!Expect(t, ref i, T.Bar)) return false;
                    shift = Keyword(t, ref i, "KEY_U_MASK_SHIFT");
                }
                var k = Key(key, 16);
                if (k == null || node == null) return false;
                DeviceKeys.Add(new DeviceKey
                {
                    Key = k,
                    Node = ParseHexUInt(node),
                    Uv = uv != null ? ParseHexUInt(uv) : 0,
                    UMaskShift = shift != null ? (int)(ParseHexUInt(shift) & 0xFF) : 0,
                });
                return i == t.Count;
            }
            if (t[0].Type == T.EntryId && t[0].Text == "PK")
            {
                i = 1;
                var k = Key(HexList(t, ref i), 16);
                if (k == null) return false;
                ProcessingKeys.Add(k);
                return i == t.Count;
            }
            if (t[0].Type == T.EntryId && t[0].Text == "HC")
            {
                i = 1;
                string priv = Keyword(t, ref i, "HOST_PRIV_KEY");
                if (!Expect(t, ref i, T.Bar)) return false;
                string cert = Keyword(t, ref i, "HOST_CERT");
                // HOST_NONCE and HOST_KEY_POINT may follow; libaacs ignores them.
                var p = Key(priv, 20);
                var c = Key(cert, 92);
                if (p == null || c == null) return false;
                HostCerts.Add(new HostCert { PrivKey = p, Cert = c });
                return true;
            }
            if (t[0].Type == T.Hex)
            {
                var id = Key(t[0].Text, 20);
                if (id == null || t.Count < 3 || t[1].Type != T.Equals || t[2].Type != T.Title) return false;
                var e = new DiscEntry { DiscId = id, Title = t[2].Text };
                i = 3;
                while (i < t.Count)
                {
                    if (t[i].Type != T.EntryId) return false;
                    string id2 = t[i++].Text;
                    switch (id2)
                    {
                        case "D":
                            // date: DIGIT - DIGIT - DIGIT
                            while (i < t.Count && (t[i].Type == T.Digit || t[i].Type == T.Hyphen)) i++;
                            break;
                        case "M": e.Mk = Key(HexList(t, ref i), 16); break;
                        case "I": e.Vid = Key(HexList(t, ref i), 16); break;
                        case "V": e.Vuk = Key(HexList(t, ref i), 16); break;
                        case "B":
                        case "P":
                        case "T":
                        case "U":
                            var pairs = new List<byte[]>();
                            while (i < t.Count && t[i].Type == T.Digit)
                            {
                                i++;
                                if (!Expect(t, ref i, T.Hyphen)) return false;
                                string hex = HexList(t, ref i);
                                if (id2 == "U")
                                {
                                    var k = Key(hex, 16);
                                    if (k == null) return false;
                                    pairs.Add(k);
                                }
                                if (!Expect(t, ref i, T.Bar)) break;
                            }
                            if (id2 == "U" && e.UnitKeys == null) e.UnitKeys = pairs;
                            break;
                        default:
                            return false;
                    }
                }
                // libaacs keeps every match and uses the one that appears last in the file.
                Disc = e;
                return true;
            }
            return false;
        }

        private static uint ParseHexUInt(string hex)
        {
            // strtoul(.., 16): long values keep their low 32 bits here.
            ulong v = 0;
            foreach (char c in hex) v = (v << 4) | (uint)Big.HexDigit(c);
            return (uint)v;
        }

        // ------------------------------------------------------------ simple files (aacskeys format)

        private static string HexAt(string line, int n)
        {
            line = line.TrimStart(' ', '\t');
            if (line.Length < n) return null;
            for (int i = 0; i < n; i++) if (!Big.IsHexDigit(line[i])) return null;
            if (line.Length > n && Big.IsHexDigit(line[n])) return null;
            return line.Substring(0, n);
        }

        private void ParsePkFile(string text)
        {
            int added = 0;
            foreach (var line in text.Split('\n'))
            {
                string hex = HexAt(line, 32);
                if (hex == null) continue;
                var k = Big.HexToBytes(hex);
                bool dup = false;
                foreach (var e in ProcessingKeys) if (Util.Bytes.Equal(e, 0, k, 0, 16)) dup = true;
                if (!dup) { ProcessingKeys.Insert(0, k); added++; }
            }
            Notes.Add(PkFile + ": " + added + " processing key(s).");
        }

        private void ParseCertFile(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            string priv = lines.Length > 0 ? HexAt(lines[0], 40) : null;
            string cert = lines.Length > 1 ? HexAt(lines[1], 184) : null;
            if (priv == null || cert == null)
            {
                Notes.Add(CertFile + ": not understood.");
                return;
            }
            HostCerts.Insert(0, new HostCert { PrivKey = Big.HexToBytes(priv), Cert = Big.HexToBytes(cert) });
            Notes.Add(CertFile + ": 1 host certificate.");
        }
    }
}
