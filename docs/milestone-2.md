# Milestone 2: multi-pass, resumable rescue (local file)

The app reads the disc in several passes, in the style of GNU `ddrescue`. It copies everything that
reads easily first and then returns to the damaged areas. All progress is saved, so a run can be
paused and resumed at any time, even after the app is closed. The image goes to the app's
**LocalState**, on the Xbox internal storage. There's no network yet; that comes in milestone 3.

## Why multi-pass (results of the first full read)

The first full read of the test DVD (build 0.1.5.0, one pass with 5 block retries and 5 sector retries) showed:

- **LBA 0 to 2,151,871 (64.9%) read without a single error**, at 2.9–5.9 MiB/s.
- From **LBA 2,151,872** (in layer 1, ~270,000 sectors after the layer change) there is a damaged
  area with a regular pattern: **16 sectors fail, 48 read fine, repeating every 64 sectors**.
  - 16 sectors is exactly one DVD **ECC block**.
  - ~64 sectors is roughly **one revolution** of the disc at that radius.
  - So the same spot fails on every revolution: a **radial scratch or defect** at a fixed angle.
- Each failing read takes **~6 s** and returns `05/20/00 INVALID COMMAND OPERATION CODE`. This is
  the Xbox drive firmware's odd way of reporting a read failure, since the same command works on
  other sectors. It is treated as an ordinary read failure.
- Retries recover very little: one sector on attempt 6, another on attempt 2.
- With immediate retries every ECC block cost ~100 commands (~10 minutes per revolution). In 1h26 the
  read advanced only ~460 sectors.

## Second run: the multi-pass rescue (build 0.1.6.0)

- Pass 1 read **LBA 0 to 2,151,871 again without errors** in 20 minutes, the same 64.86% as before.
- From LBA 2,151,872 **everything failed**, including sectors that read fine in the first run
  (2,151,888–2,151,935). Pass 1 jumped through the rest of the disc and **all 9 samples between
  LBA 2,156,032 and 3,196,928 failed**:
  - up to LBA ~2.41M: `05/20/00` after ~6.5–8.5 s;
  - from LBA ~2.67M: a 3-byte truncated response, `38 00 02` (Length 56 + status CHECK CONDITION,
    sense dropped by the driver), after only ~1.5–2 s.
- In pass 1b, after a few blocks that still followed the 16-bad/48-good pattern, every block from
  LBA 2,152,064 failed. It was paused at LBA 2,155,727.
- MODE SENSE works on this drive: the error recovery page reports **read retry count 24**, which
  explains the ~6.5 s per failing read.
- Result: ok 2,151,968 (64.86%), pending 4,336, not tried 1,161,495. That is 2.22 GiB of the disc
  (81% of layer 1) still unread.

Two explanations fit, and the **readability scan** tells them apart:
1. **The disc** is unreadable on this drive past ~LBA 2.15M (layer 1 damage, e.g. delamination or
   disc rot, which would also explain the PC drive's `ID CRC OR ECC ERROR`).
2. **The drive** gets into a bad state after hitting the defect and then fails good sectors too.

### Readability scan (Scan disc)

Reads one 16-sector ECC block at about 90 spots across the disc, once each, and writes nothing.
After every failure it re-reads a reference block in layer 0 that is known to be good. If the
reference also fails, it's the drive, and the scan logs how long the drive takes to recover. The
log marks each spot with its layer and the DVD file it belongs to. **Run it right after a full
power-off of the console** (hold the power button 10 s, unplug for 30 s), before any rescue.

**Detect disc** now also lists the DVD files (ISO9660) with their LBA ranges, and the rescue
summary shows how much of each file has been read. So it's possible to tell which part of the
movie is missing.

## How the rescue works

| Pass | What it does | Status after |
|------|--------------|--------------|
| **1 - fast copy** | Reads in blocks of `SectorsPerRead`. A failing block is split into 16-sector ECC blocks, each read **once**. A failing ECC block becomes *pending* and reading moves on. For 2048 sectors after an error it reads ECC-block-sized pieces, so one bad block doesn't take the good ones down with it. Where errors are dense (4 failures within 4096 sectors) it **jumps ahead** 4096 sectors, doubling on each consecutive jump (up to 262,144), and leaves the jumped area *not tried*. | `+` ok, `*` pending, `?` skipped |
| **1b - fill** | Reads the areas skipped by pass 1, with the same rules but no jumping. | `+` / `*` |
| **2 - scrape** | Reads every *pending* sector once, one at a time, at `RetryReadSpeedKBps` (slower). | `+` / `-` bad |
| **3+ - retry** | `RetryPasses` rounds over the *bad* sectors, one attempt per sector per round. | `+` / `-` |

On the damaged area of the test DVD, pass 1 costs about **6 s per revolution** instead of ~10 minutes.
Combined with the jumps, the readable part of the disc is secured first.

- **Pause and resume:** **B** (or *Cancel*) pauses at the end of the current command. **Rescue /
  resume** continues from where it stopped, also after the app was closed or killed. The map is
  saved every 30 s and at the end of every pass.
- **Retry failed:** each press runs the normal passes (if anything is left) plus **one more retry pass**
  over the bad sectors, even when `RetryPasses` is used up. On a healthy disc pass 1 reads everything,
  the hash is computed and nothing else runs.
- **Hash:** once nothing is left to try (no `?`/`*` and the retry passes are done), the app computes
  the **SHA-256 of the image file** and writes `VOLUME.sha256`. It can't be computed while reading,
  because the passes write the data out of order.

### Experimental drive settings (only the console can tell whether they help)

- **`RetryReadSpeedKBps`** (default 1385 = DVD 1x): before the scrape and retry passes the app sends
  SET CD SPEED to slow the drive down, which often recovers scratched sectors. It returns to maximum
  speed at the end. The log line "SET CD SPEED ..." shows whether the drive accepted it.
- **`DriveReadRetryCount`** (default -1 = don't touch): the drive's internal retry count (MODE page
  01h) used during pass 1. Lowering it (e.g. `0` or `1`) may make each failing read give up in much
  less than 6 s. The change is volatile and is restored at the end of every run. The log always shows
  the drive's current value ("Drive error recovery page: ... read retry count N"), even when it isn't changed.

## Output files (LocalState)

| File | Contents |
|------|----------|
| `ACQUARIA.iso` | Raw image, 2048 bytes per sector, full disc size. Only sectors read OK are written; everything else is zeros. |
| `ACQUARIA.map` | Rescue state: every sector of the disc as `start count status [last error]` ranges (see below). This file is what makes resuming possible. |
| `ACQUARIA.sha256` | SHA-256 of the image in `sha256sum` format, once nothing is left to try. |
| `xisoburn-YYYYMMDD-HHMMSS.log` | Session log: every failed ECC block in pass 1, every recovered sector, the jumps, progress every 5% of each pass. |
| `config.json` | Settings. |

### `.map` format

```
# xISOBurn rescue map v1
# volume: ACQUARIA
# total_sectors: 3317799
# image: ACQUARIA.iso
# retry_passes_done: 0
# summary: ok 3,301,000 (99.494%) | not tried 0 | pending 0 | bad 16,799
# status: + read OK | ? not tried yet | * failed once, pending retry | - unreadable so far (zeros in the image)
# start count status [last error]
0 2151872 +
2151872 9 -  05/20/00 INVALID COMMAND OPERATION CODE
2151881 1 +
...
```

The ranges cover the whole disc in order (LBA = absolute sector number, 2048 bytes each). Every
sector marked `-`, `*` or `?` is zeros in the image.

## config.json

```json
{
  "SectorsPerRead": 32,
  "TimeoutSeconds": 20,
  "SkipOnErrors": true,
  "RetryPasses": 2,
  "RetryReadSpeedKBps": 1385,
  "DriveReadRetryCount": -1,
  "StartSector": 0,
  "SectorLimit": 0,
  "WriteImage": true,
  "OutputName": ""
}
```

| Key | Meaning |
|-----|---------|
| `SectorsPerRead` | Sectors per read in good areas, 1–64. |
| `TimeoutSeconds` | Limit per command, 5–120. Failing reads took ~6 s on the Xbox drive. |
| `SkipOnErrors` | Pass 1 jumps over dense error areas (filled later by pass 1b). |
| `RetryPasses` | Rounds over the bad sectors after the scrape. To keep trying later, raise it and run again. |
| `RetryReadSpeedKBps` | Drive speed for scrape/retry, in kB/s; 0 = don't change. |
| `DriveReadRetryCount` | Drive-internal retries during pass 1; -1 = don't change. Experimental. |
| `StartSector`, `SectorLimit` | Work range (for tests). The image and the map always cover the whole disc. |
| `WriteImage` | `false` = build the map without writing the image. **Don't switch modes halfway**: a map built with `false` marks sectors as read without their data. To restart, delete the `.iso` and `.map`. |
| `OutputName` | File name without extension; empty = volume label. |

The app rewrites `config.json` after loading it, so the file always lists every current key. Old
keys from previous versions (`BlockRetries`, `SectorRetries`) are dropped. Edit it through the
Device Portal (see [install.md](install.md), section 5).

## Test plan

1. **Install the new version** (remove the previous one first). You can delete the old files in
   LocalState (`*.iso`, `*.map`, `*.bin`, old logs). `config.json` can stay; it is updated automatically.
2. **Optional, to test `DriveReadRetryCount`:** set `"DriveReadRetryCount": 0`. In the log, check the
   "Drive error recovery page" and "MODE SELECT" lines, and whether failing reads now take less than 6 s.
3. Choose **Rescue / resume** with the defaults (`WriteImage: true`). Pass 1 should cross the good
   part at full speed and jump over the damaged area.
4. Leave it running; you can pause with **B** and resume whenever you like. Expected order: pass 1
   to the end of the disc, then pass 1b in the damaged area, then the scrape and the retries.
5. Share the log and the `.map`: how many sectors are left bad, whether the slower speed recovered
   any, and how long each phase took.

### Things to check on the console

- Whether LocalState accepts the 6.3 GiB image (it is reserved at the start; if there's no space,
  the run fails immediately with the error in the log).
- Whether SET CD SPEED and MODE SELECT are accepted, and whether they change the time per failing read.
- Pause/resume: close the app halfway and resume; the "Resuming from ACQUARIA.map" line should
  show the earlier progress.
- Cleaning the disc (straight lines from the centre outwards, soft microfiber) before the scrape pass
  can recover more than any setting.
