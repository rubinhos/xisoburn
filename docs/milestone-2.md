# Milestone 2: full read with error handling (local file)

The app reads the whole disc, retrying first per block and then per sector. Sectors that won't read
become zeros and are recorded in a map. The result goes to the app's **LocalState**, on the Xbox
internal storage. There's no network yet; that comes in milestone 3.

## How reading works

1. **Disc size:** taken from the DVD structure (OTP calculation) and checked against the ISO9660
   volume. The Xbox drive's READ CAPACITY gives a wrong value for an OTP DVD-9 (see
   [spike.md](spike.md)).
2. **Reads in blocks** of `SectorsPerRead` sectors (default 32 = 64 KiB) with READ(10).
3. If a block fails, it is read again **`BlockRetries`** times (default 5).
4. If it keeps failing, reading switches to **sector by sector**, with 1 + **`SectorRetries`**
   attempts each (default 5).
5. A sector that won't read is **zero-filled** and recorded in the map. The copy **never stops**
   because of a bad sector. It only stops if the disc leaves the drive (`02/3A`), if you cancel, or
   if writing fails.
6. **SHA-256** is computed while reading, over exactly the bytes written (zeros included).
7. When a failure comes back without sense data, the app sends REQUEST SENSE to try to find the cause.

## Output files (LocalState)

| File | Contents |
|------|----------|
| `ACQUARIA.iso` | Raw image, 2048 bytes per sector (only when `WriteImage` = true) |
| `ACQUARIA.map` | Sectors **not read** (zero-filled), as `start-end (count) error` ranges, plus the SHA-256 |
| `ACQUARIA.sha256` | The hash in `sha256sum` format (only when the copy completes) |
| `xisoburn-YYYYMMDD-HHMMSS.log` | Session log: every failed attempt with its sense data, plus progress every 5% |
| `config.json` | Settings (created on first launch) |

A partial copy (`StartSector`/`SectorLimit`) gets the suffix `_lbaSTART-END` in its name. If the
copy is cancelled, the image is cut where it stopped and the `.map` says "INTERRUPTED".

## config.json

```json
{
  "SectorsPerRead": 32,
  "BlockRetries": 5,
  "SectorRetries": 5,
  "TimeoutSeconds": 20,
  "StartSector": 0,
  "SectorLimit": 0,
  "WriteImage": true,
  "OutputName": ""
}
```

- `SectorsPerRead`: 1 to 64 (the spike confirmed up to 64).
- `TimeoutSeconds`: 5 to 120, per command. A bad sector can take several seconds per attempt. Worst
  case for a 32-sector block where every sector is bad: 6 + 32 × 6 = 198 commands.
- `SectorLimit`: 0 means up to the end. Use it to test a range, for example `200000` (about 390 MiB).
- `WriteImage`: `false` reads the whole disc and builds the map and hash **without writing the image**.
  Use it to find out whether the Xbox drive can read the whole disc before spending 6.3 GiB of storage.
- `OutputName`: file name without extension; empty uses the volume label.

To edit it without a keyboard on the console, download `config.json` from the Device Portal, edit it
on the PC and upload it back (see [install.md](install.md), section 5). The file is re-read before
every copy.

## Suggested test plan

1. **Install the new version.** Remove the old one first if the Device Portal complains about the signature.
2. Open the app and choose **Detect disc**. The "Disc" panel should show DVD-ROM, 2 layers (OTP),
   **3,317,799 sectors (6.33 GiB)**, volume ACQUARIA, and the layer change at LBA 1,881,392.
3. **Short test:** set `SectorLimit: 50000` and choose **Copy to LocalState**. Check that the speed
   and remaining time make sense. Try **B** to cancel partway through.
4. **Full read without writing** (`SectorLimit: 0`, `WriteImage: false`). This is the main test: it
   shows whether the Xbox drive reads the sectors the PC drive fails on. Share the `.map` and the log.
5. If step 4 goes well, run the **full copy with writing** (`WriteImage: true`). The app reserves
   6.3 GiB up front; if there isn't enough space it fails right at the start, with the error in the log.

### Things to check on the console

- The drive's **sustained speed** (the spike didn't measure it properly).
- **How long** each bad sector takes, and whether retries recover any (the log says "read on attempt N").
- Whether errors arrive with sense data (`03/11/00`...) or as a "short response", and whether REQUEST SENSE helps.
- **Start of layer 1** (LBA 1,881,392): in the spike, the first 16 sectors there came back zeroed.
  In the full read, check whether the log or the map shows anything unusual in that region.
- **Space:** whether LocalState accepts a 6.3 GiB file on internal storage.
- **Suspension:** what happens if the console dims the screen or you press the Xbox button during a copy.
