using System.Collections.Generic;
using System.Text;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>One track of a CD, from the TOC (LBA range, end exclusive).</summary>
    internal sealed class CdTrack
    {
        public int Number;
        /// <summary>The Q-channel control bits: 0x04 = data track, 0x01 = pre-emphasis (audio).</summary>
        public int Control;
        public long Lba, End;
        /// <summary>Data track mode from the sector header (1 or 2), 0 for audio or unknown.</summary>
        public int Mode;

        public bool Audio { get { return (Control & 0x04) == 0; } }

        public string CueType
        {
            get { return Audio ? "AUDIO" : Mode == 2 ? "MODE2/2352" : "MODE1/2352"; }
        }
    }

    /// <summary>
    /// The table of contents of a CD (READ TOC formats 0 and 1). Audio and mixed-mode CDs are copied raw
    /// (2352 bytes per sector, READ CD) into NAME.bin with NAME.cue next to it. Only the first session is
    /// copied: on a multi-session disc (CD-Extra) the gap before the next session cannot be read.
    /// </summary>
    internal sealed class CdToc
    {
        /// <summary>Lead-out (6750) + lead-in (4500) + pre-gap (150) between two sessions.</summary>
        private const int SessionGap = 11400;

        public readonly List<CdTrack> Tracks = new List<CdTrack>();
        /// <summary>Tracks of later sessions, not copied.</summary>
        public readonly List<CdTrack> OtherSessions = new List<CdTrack>();
        public long LeadOut;
        /// <summary>End of the first session (exclusive): what is copied.</summary>
        public long FirstSessionEnd;

        public bool HasAudio
        {
            get { foreach (var t in Tracks) if (t.Audio) return true; return false; }
        }

        /// <summary>
        /// Parses a format 0 response (4-byte header, then 8-byte descriptors: control in byte 1, track
        /// number in byte 2, LBA in bytes 4-7; track 0xAA is the lead-out) and, if given, a format 1
        /// response (first track of the last session in byte 6, its LBA in bytes 8-11). Null if unusable.
        /// </summary>
        public static CdToc Parse(byte[] toc, int length, byte[] sessions, int sessionsLength)
        {
            if (length < 4 + 8) return null;
            int dataLength = Bytes.BE16(toc, 0) + 2;
            int end = System.Math.Min(length, dataLength);
            var t = new CdToc();
            for (int p = 4; p + 8 <= end; p += 8)
            {
                int number = toc[p + 2];
                long lba = (int)Bytes.BE32(toc, p + 4);
                if (number == 0xAA) { t.LeadOut = lba; continue; }
                if (number < 1 || number > 99) continue;
                t.Tracks.Add(new CdTrack { Number = number, Control = toc[p + 1] & 0x0F, Lba = lba });
            }
            if (t.Tracks.Count == 0 || t.LeadOut <= 0) return null;
            t.Tracks.Sort((a, b) => a.Lba.CompareTo(b.Lba));

            // Multi-session: keep the tracks of the first session.
            t.FirstSessionEnd = t.LeadOut;
            if (sessions != null && sessionsLength >= 12)
            {
                int lastSessionFirstTrack = sessions[6];
                long lastSessionLba = (int)Bytes.BE32(sessions, 8);
                if (lastSessionFirstTrack > t.Tracks[0].Number && lastSessionLba > SessionGap)
                {
                    t.FirstSessionEnd = lastSessionLba - SessionGap;
                    for (int i = t.Tracks.Count - 1; i >= 0; i--)
                    {
                        if (t.Tracks[i].Number < lastSessionFirstTrack) continue;
                        t.OtherSessions.Insert(0, t.Tracks[i]);
                        t.Tracks.RemoveAt(i);
                    }
                }
            }
            for (int i = 0; i < t.Tracks.Count; i++)
                t.Tracks[i].End = i + 1 < t.Tracks.Count ? t.Tracks[i + 1].Lba : t.FirstSessionEnd;
            return t.Tracks.Count > 0 && t.FirstSessionEnd > t.Tracks[0].Lba ? t : null;
        }

        private static string Msf(long lba)
        {
            if (lba < 0) lba = 0;
            return (lba / 4500).ToString("00") + ":" + (lba / 75 % 60).ToString("00") + ":" + (lba % 75).ToString("00");
        }

        /// <summary>The cue sheet for a single raw image (the .bin starts at LBA 0).</summary>
        public string ToCue(string binName)
        {
            var sb = new StringBuilder();
            sb.Append("FILE \"").Append(binName.Replace("\"", "'")).Append("\" BINARY\n");
            foreach (var t in Tracks)
            {
                sb.Append("  TRACK ").Append(t.Number.ToString("00")).Append(' ').Append(t.CueType).Append('\n');
                if (t.Audio && (t.Control & 0x01) != 0) sb.Append("    FLAGS PRE\n");
                sb.Append("    INDEX 01 ").Append(Msf(t.Lba)).Append('\n');
            }
            return sb.ToString();
        }

        public string Describe()
        {
            int audio = 0, data = 0;
            foreach (var t in Tracks) if (t.Audio) audio++; else data++;
            return Tracks.Count + " track(s): " + audio + " audio, " + data + " data" +
                   (OtherSessions.Count > 0 ? " (+" + OtherSessions.Count + " in a later session, not copied)" : "");
        }
    }
}
