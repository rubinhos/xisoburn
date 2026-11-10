// SPDX-License-Identifier: LGPL-2.1-or-later
// BD+ session for a whole-disc copy: the sequence of libbdplus examples/bdplus_test.c (init with the
// Volume ID and media key, start, BDPLUS_RUN_CONVTAB, title events, then every clip), or a cached
// conversion table named after the media key (libbdplus internal.c bdplus_disc_findcachefile).
// Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN, LGPL-2.1-or-later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace xISOBurn.Bluray.Bdplus
{
    internal sealed class BdplusSession
    {
        private readonly string _folder;   // keys/bdplus
        private readonly Action<string> _log;

        /// <summary>Patches per clip number (00001.m2ts = 1).</summary>
        public readonly Dictionary<uint, List<BdplusPatch>> Patches = new Dictionary<uint, List<BdplusPatch>>();
        public string Source { get; private set; }

        public BdplusSession(string folder, Action<string> log)
        {
            _folder = folder;
            _log = log;
        }

        private string ConvtabFolder { get { return Path.Combine(_folder, "convtab"); } }

        /// <summary>
        /// Builds the patch lists. Runs synchronously (the VM reads disc files through 'read'), so call it
        /// on a background thread. Returns false if no conversion table could be obtained.
        /// </summary>
        public bool Run(byte[] svm, byte[] vid, byte[] mk, IList<uint> clips, BdplusFileRead read, CancellationToken ct)
        {
            ConvTable table = mk != null ? LoadCached(mk) : null;
            if (table != null)
            {
                Source = "cached conversion table";
            }
            else
            {
                if (vid == null)
                {
                    _log("BD+: no Volume ID, so the BD+ program of the disc cannot run (add the disc's | I | entry to KEYDB.cfg).");
                    return false;
                }
                var config = BdplusConfig.Load(Path.Combine(_folder, "vm0"));
                foreach (var n in config.Notes) _log("BD+: " + n + ".");
                var m = new BdplusMachine(config, read, _log);
                m.Cancelled = () => ct.IsCancellationRequested;
                string err = m.LoadSvm(svm);
                if (err != null)
                {
                    _log("BD+: BDSVM/00000.svm: " + err + ".");
                    return false;
                }
                _log("BD+: content code generation " + m.Gen + " (" + (m.Date >> 16) + "-" + ((m.Date >> 8) & 0xff).ToString("D2") + "-" +
                     (m.Date & 0xff).ToString("D2") + ")" + (m.Gen > 3 ? "; libbdplus only knows generations up to 3, this may fail" : "") + ".");
                Buffer.BlockCopy(vid, 0, m.VolumeId, 0, 16);
                if (mk != null) Buffer.BlockCopy(mk, 0, m.MediaKey, 0, 16);
                else _log("BD+: no media key: the content code may fail.");
                string slots = Path.Combine(_folder, "slots.bin");
                m.LoadSlots(slots);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                m.RunInit();                               // bdplus_start
                m.RunInit();                               // BDPLUS_RUN_CONVTAB: run_init, then run_convtab
                m.RunConvTab();
                for (uint t = 0; t < 32 && !ct.IsCancellationRequested; t++) m.RunTitle(t);
                if (m.ConvTab == null)
                {
                    _log("BD+: the content code sent no conversion table (" + m.Instructions.ToString("N0") + " instructions, " +
                         sw.Elapsed.TotalSeconds.ToString("0") + " s).");
                    m.SaveSlots(slots);
                    return false;
                }
                foreach (var c in clips)
                {
                    if (ct.IsCancellationRequested) break;
                    m.RunM2ts(c);
                }
                m.RunShutdown();
                m.SaveSlots(slots);
                ct.ThrowIfCancellationRequested();
                table = m.ConvTab;
                Source = "content code (" + m.Instructions.ToString("N0") + " instructions, " + sw.Elapsed.TotalSeconds.ToString("0") + " s)";
                if (mk != null) SaveCached(mk, table);
            }

            foreach (var c in clips)
            {
                var p = table.PatchesFor(c);
                if (p != null && p.Count > 0) Patches[c] = p;
            }
            int total = 0;
            foreach (var kv in Patches) total += kv.Value.Count;
            _log("BD+: conversion table from the " + Source + ": " + table.Tables.Count + " table(s); " + Patches.Count +
                 " clip(s) get " + total.ToString("N0") + " patch(es).");
            return true;
        }

        private ConvTable LoadCached(byte[] mk)
        {
            string mkHex = Big.Hex(mk).ToLowerInvariant();
            if (!Directory.Exists(ConvtabFolder)) return null;
            foreach (var f in Directory.GetFiles(ConvtabFolder))
            {
                string name = Path.GetFileName(f).ToLowerInvariant();
                if (name.Length < 36 || !name.StartsWith(mkHex) || !name.EndsWith(".bin")) continue;
                string err;
                var t = ConvTable.Load(File.ReadAllBytes(f), out err);
                if (t != null)
                {
                    _log("BD+: using the cached conversion table " + Path.GetFileName(f) + ".");
                    return t;
                }
                _log("BD+: cached conversion table " + Path.GetFileName(f) + " not usable: " + err + ".");
            }
            return null;
        }

        private void SaveCached(byte[] mk, ConvTable t)
        {
            try
            {
                Directory.CreateDirectory(ConvtabFolder);
                File.WriteAllBytes(Path.Combine(ConvtabFolder, Big.Hex(mk).ToLowerInvariant() + ".bin"), t.SaveActive());
            }
            catch (Exception ex)
            {
                _log("BD+: could not save the conversion table: " + ex.Message);
            }
        }
    }
}
