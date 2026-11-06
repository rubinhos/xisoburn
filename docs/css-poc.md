# CSS proof of concept

**Status:** written, not yet run on a console. Compiled only on GitHub Actions. The cipher port
couldn't be compared with the C original on a PC, because no C compiler was available. The run on
the console checks it instead (see [How the test checks itself](#how-the-test-checks-itself)).

## Why

xISOBurn reads the console's drive through `Windows.Devices.Custom` and SCSI pass-through (from
[XBSX2 PR #11](https://github.com/XboxEmulationHub/XBSX2/pull/11)). On CSS-protected DVD-Video, the
drive refuses the scrambled sectors (milestone 2): it serves them only after the host authenticates.

On a PC, [libdvdcss](https://code.videolan.org/videolan/libdvdcss) does that authentication with
`IOCTL_DVD_*` requests. Those are blocked on the console, and XBSX2 only ever sends read (DATA IN)
commands. So the open question is whether the console lets **SEND KEY** through. SEND KEY is a
DATA OUT command, and CSS can't work without it.

This POC answers that question in one run, and if SEND KEY works it goes on to descramble a sample.

## What was ported, and from where

Source: libdvdcss, `code.videolan.org/videolan/libdvdcss`, commit
`811ce97a1d4d1c9d936f4b2707f533a9ef768251` (GPL-2.0-or-later, Copyright VideoLAN).

| xISOBurn file | libdvdcss source | Notes |
|---------------|------------------|-------|
| `Css/CssTables.cs` | `src/csstables.h` | Generated mechanically; table sizes checked (256/512/288) |
| `Css/CssCipher.cs` | `src/css.c`: `CryptKey`, `DecryptKey`, `DecryptDiscKey` (+ `player_keys`), `DecryptTitleKey`, `dvdcss_unscramble` | Ported line by line; the `uint8_t` truncations that C does implicitly are written out |
| `Css/CssDrive.cs` | `src/ioctl.c`, raw-SCSI branches (Solaris USCSI / Haiku) | Same CDB bytes and response offsets, sent through `OpticalDrive` |
| `Core/CssTest.cs` | `src/css.c`: `GetBusKey`, `dvdcss_disckey`, `dvdcss_titlekey` | Same sequence, one log line per command |

Not ported (not needed to answer the question): disc key cracking (`CrackDiscKey`), title key cracking
(`CrackTitleKey`), the key cache, CPPM/CPRM.

## The steps

**CSS test** (next to **Scan disc**) runs these steps and logs the SCSI result of each one:

| # | Step | Commands | What a failure means |
|---|------|----------|----------------------|
| 1 | Regions | READ DVD STRUCTURE fmt 01h, REPORT KEY RPC (08h), ASF (05h) | Read only; logs the disc and drive regions |
| 2 | Baseline | READ(10) of the first 64 sectors of the first title VOB | Expected to fail, as in milestone 2 |
| 3 | Bus key | REPORT KEY AGID, **SEND KEY challenge**, REPORT KEY KEY1, REPORT KEY challenge, **SEND KEY KEY2** | Shows which step failed. A failure at *SEND KEY challenge* means the console blocks DATA OUT |
| 4 | Disc key | READ DVD STRUCTURE fmt 02h, ASF, decryption with 31 player keys | ASF ≠ 1: the region doesn't match |
| 5 | Title key | new bus key, REPORT KEY title key (04h) at the VOB's LBA, ASF | Refused: region, or no key |
| 6 | Read and descramble | READ(10) of up to 2048 sectors (4 MiB), descrambling, MPEG check | Saved as `css-test-YYYYMMDD-HHMMSS.vob` |

The test never sends SEND KEY RPC (format 06h), so it can't change the drive's region or use up any
of its region changes. The handshake is the same one every DVD player does.

## How the test checks itself

Each part of the port is checked by the drive, or by the data:

- **CryptKey:** the drive's KEY1 must match one of the 32 variants. *KEY1 matches none of the 32
  variants* points at the port, or at a drive that won't authenticate.
- **SEND KEY KEY2 accepted:** the drive itself checks the host key that was computed.
- **DecryptKey and the player keys:** the disc key block contains the disc key encrypted with itself,
  and a decryption only counts if that check passes.
- **Title key and descrambling:** after descrambling, the MPEG start codes (`00 00 01`) in the
  payload are counted before and after. Scrambled bytes give about one per 8600 sectors, and real
  MPEG-2 gives many. The log shows both counts.
- **The final check:** download `css-test-*.vob` from the web page (*CSS test samples* section)
  and open it in VLC.

## How to run it

1. Push to `master`, wait for the **build** workflow, and install the package (see
   [install.md](install.md)).
2. Insert a commercial DVD-Video from the same region as the console.
3. **Detect disc** should report `Protection: CSS/CPPM`.
4. **CSS test**. It takes seconds. The status line shows the verdict.
5. Get the log (web page → *Log files*) and look at the `=== CSS test ... ===` section.

## Possible results

| Status line | Meaning | Next step |
|-------------|---------|-----------|
| `SUCCESS: the Xbox drive authenticated and the VOB was descrambled` | Everything works | Integrate into the copy (authenticate before the rescue, decrypt per title) |
| `Authentication failed at SEND KEY challenge (the Xbox may block DATA OUT commands)` | The console filters DATA OUT | Look at the HRESULT and sense in the log; try `IOCTL_SCSI_PASS_THROUGH_DIRECT` or a different `IOControlAccessMode` |
| `... failed at KEY1 check` | KEY1 doesn't match | Compare `CryptKey` with the C original (a bug in the port) |
| `ASF is not 1 after reading the disc key` | Region mismatch | Use a disc from the console's region |
| `Keys obtained, but the drive still refuses the VOB sectors` | The drive wants something else | Compare with libdvdcss on a PC |
| `... does not look like MPEG-2` | Wrong title key | Check the title-key and disc-key steps in the log |

## License

The files above are derived from libdvdcss and stay under GPL-2.0-or-later. If xISOBurn is ever
distributed with them, it has to be under a GPL-compatible license.
