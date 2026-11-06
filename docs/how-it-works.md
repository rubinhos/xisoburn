# How xISOBurn works

This is the technical reference: how the app reaches the drive, how a copy runs, how CSS discs are
decrypted, the files it writes, `config.json`, and the web page. To build and install, see
[install.md](install.md).

## Drive access

On the console, `CreateFile("\\.\X:")` and the `IOCTL_CDROM` / `IOCTL_DVD` requests are blocked. What
works in Developer Mode is a technique taken from
[XBSX2 PR #11](https://github.com/XboxEmulationHub/XBSX2/pull/11):

1. Find the CD-ROM device interface with `DeviceInformation.FindAllAsync` and the AQS filter for
   `GUID_DEVINTERFACE_CDROM` (`{53f56308-b6bf-11d0-94f2-00a0c91efb8b}`).
2. Open it with `CustomDevice.FromIdAsync(id, ReadWrite, Shared)`. The manifest needs the
   `optical` device capability and the interface GUID.
3. Send `IOCTL_SCSI_PASS_THROUGH` (`0x4D004`, buffered) with `SendIOControlAsync`. The
   `SCSI_PASS_THROUGH` header, the sense data and the data travel in one buffer.

Any MMC command can then be sent, both DATA IN (reads) and DATA OUT (MODE SELECT, SEND KEY). The code
is in `xbox-app/xISOBurn/Scsi/OpticalDrive.cs`.

What the Series X drive does:

- **The drive** is a PLDS DG-6M5S, firmware 014R. Reads of 16 to 64 sectors per command work.
- **READ CAPACITY** is wrong on dual-layer OTP discs: it returns `end − start + 1` of the physical
  sector numbers. The disc size comes from the DVD physical format (READ DVD STRUCTURE format 0),
  checked against the ISO 9660 volume size. READ CAPACITY is only a last resort, for CD and BD.
- **Errors can come without sense data:** a 3-byte response (`38 00 02`: length 56 + CHECK
  CONDITION). The app then asks with REQUEST SENSE.
- **A failing read on a damaged area takes about 6 s.** The drive's internal retry count is 24
  (MODE page 01h). It often reports `05/20/00` instead of a medium error.

## The copy

The copy runs in several resumable passes, in the style of
[GNU ddrescue](https://www.gnu.org/software/ddrescue/):

| Pass | What it does | Sector state after |
|------|--------------|--------------------|
| **1 – fast copy** | Reads `SectorsPerRead` sectors at a time. A failing block is split into 16-sector ECC blocks, each read once. A block that still fails becomes *pending* and the copy moves on. After an error it reads ECC-block-sized pieces for 2048 sectors. Where errors are dense (4 failures within 4096 sectors) it jumps ahead 4096 sectors, doubling on each consecutive jump up to 262,144, and leaves the area *not tried*. | `+` ok, `*` pending, `?` not tried |
| **1b – fill** | The areas pass 1 skipped, without jumping. | `+` / `*` |
| **2 – scrape** | Every pending sector once, one at a time, at `RetryReadSpeedKBps`. | `+` / `-` bad |
| **3+ – retry** | `RetryPasses` rounds over the bad sectors, one attempt per sector per round. | `+` / `-` |
| **Decrypt pass** | Only when a CSS copy is resumed: decrypts any video sector the image still holds scrambled (see below). | — |

- **Pause and resume:** **B** pauses at the end of the current command, and **Copy / resume**
  continues, even after the app was closed. The map is saved every 30 s and at the end of every pass.
- **Retry failed** runs one more retry pass over the bad sectors, even after `RetryPasses` is used up.
- **Zero-fill:** only sectors that read correctly are written. Everything else stays zeros in the
  image and is listed in the map.
- **SHA-256:** computed from the image file once nothing is left to try. It can't be computed while
  reading, because the passes write out of order.
- **Quick refusals:** a read that fails in under 200 ms, or with sense `05/6F/xx`, is the drive
  refusing the area, not disc damage. It happens with copy protection or a region mismatch. The
  copy counts these (`refused N`) and explains them on screen and in the log.
- **Scan disc** is a read-only health check. It reads one ECC block at about 90 points across the
  disc. After every failure it re-reads a known-good block, which tells disc damage from a drive that
  got stuck. The log names the DVD file each point belongs to.

## CSS-protected DVD-Video

Most commercial DVD-Video discs use CSS. Before authentication, the drive refuses the scrambled video
sectors. xISOBurn uses a C# port of [libdvdcss](https://code.videolan.org/videolan/libdvdcss)
(VideoLAN, GPL-2.0-or-later), sent through the pass-through above. When **Detect disc** reports
`Protection: CSS/CPPM` and `DecryptCss` is on, the copy works like this:

1. **Regions.** The log shows the disc's regions (READ DVD STRUCTURE format 01h) and the drive's
   (REPORT KEY RPC). The region setting is never changed: SEND KEY RPC is never sent.
2. **Authentication (bus key).** REPORT KEY AGID, SEND KEY challenge, REPORT KEY KEY1 (checked
   against the 32 variants of `CryptKey`), REPORT KEY challenge, SEND KEY KEY2. Every DVD player does
   this handshake.
3. **Disc key.** READ DVD STRUCTURE format 02h, ASF = 1, then decryption with the 31 player keys of
   libdvdcss.
4. **Title keys.** For each VOB in the ISO 9660 listing: a new bus key, then REPORT KEY title key at
   the file's first sector, decrypted with the disc key. `VTS_xx_2..9.VOB` fall back to the key of
   the previous file of the same title set.
5. **Copy.** With the drive authenticated, the video sectors read normally. Before each block is
   written, every scrambled sector (PES scrambling bits in byte `0x14`) inside a VOB with a key is
   descrambled (`dvdcss_unscramble`), and its scrambling bits are cleared. The ISO comes out
   decrypted and plays in any player.
6. **Re-authentication.** If the drive refuses a read during the copy (for example after a reset),
   the app authenticates again, up to 20 times per run, and reads the block once more.
7. **Decrypt pass.** When a copy is resumed, the VOB ranges of the image are read back from the file,
   and any sector still scrambled is decrypted in place. That covers images started by an older version
   or with `DecryptCss` off.

What isn't supported:

- Discs whose disc key none of the player keys open. libdvdcss would then attack the title keys
  (`CrackTitleKey`), which isn't ported. The video is then copied scrambled, and the log says so.
- A disc from a region the drive isn't set to. The drive refuses the keys.
- CPPM (DVD-Audio) and CPRM.

The port is in `xbox-app/xISOBurn/Css/`:

| File | Ported from libdvdcss |
|------|-----------------------|
| `CssTables.cs` | `src/csstables.h`, generated verbatim |
| `CssCipher.cs` | `CryptKey`, `DecryptKey`, `DecryptDiscKey` + `player_keys`, `DecryptTitleKey`, `dvdcss_unscramble` (`src/css.c`) |
| `CssDrive.cs` | the CDBs and response offsets of the raw-SCSI branches of `src/ioctl.c` |
| `CssSession.cs` | `GetBusKey`, `dvdcss_disckey`, `dvdcss_titlekey` (`src/css.c`) |

## Files (LocalState)

| File | Contents |
|------|----------|
| `VOLUME.iso` | Raw image, 2048 bytes per sector, full disc size. Unread sectors are zeros. On CSS discs the video is decrypted. |
| `VOLUME.map` | State of every sector (see below). Resuming depends on it. |
| `VOLUME.sha256` | SHA-256 of the image in `sha256sum` format, once nothing is left to try. |
| `xisoburn-YYYYMMDD-HHMMSS.log` | Log of one app session. |
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

The ranges cover the whole disc in order. LBA is the absolute sector number, 2048 bytes each.

## config.json

Every key can also be changed on the console under **Settings**. After loading, the app rewrites
the file with every current key, so the file always lists what the app uses.

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
  "OutputName": "",
  "DecryptCss": true,
  "WebPort": 8770,
  "WebPinRequired": true,
  "WebPin": "1234",
  "KeepAwake": false
}
```

| Key | Meaning |
|-----|---------|
| `SectorsPerRead` | Sectors per read in good areas, 1–64. |
| `TimeoutSeconds` | Limit per command, 5–120 s. |
| `SkipOnErrors` | Pass 1 jumps over dense error areas, which pass 1b fills later. |
| `RetryPasses` | Rounds over the bad sectors after the scrape, 0–100. |
| `RetryReadSpeedKBps` | Drive speed for the scrape and retries, in kB/s (1385 = DVD 1x). 0 = don't change. |
| `DriveReadRetryCount` | The drive's internal retries during pass 1 (MODE page 01h). -1 = don't change. Volatile; restored after every run. |
| `StartSector`, `SectorLimit` | Work range, for tests. The image and the map always cover the whole disc. |
| `WriteImage` | `false` builds only the map (a drive test). Don't switch it in the middle of a copy. |
| `OutputName` | File name without extension. Empty = the volume label. |
| `DecryptCss` | Decrypt CSS-protected DVD-Video while copying. `false` copies the video sectors as the drive returns them. |
| `WebPort` | Port of the web page. Changing it needs an app restart. |
| `WebPinRequired`, `WebPin` | Ask for the 4–8 digit PIN shown on the Xbox screen. |
| `KeepAwake` | Keep the console awake while the app is open: no dimming and no idle power-off. When it is off, the idle Xbox suspends the app and the web page stops answering until the app resumes. After a resume the app restarts the web page by itself. Applies at once. |

## Web page

The app serves a page on the local network (`StreamSocketListener`, capability
`privateNetworkClientServer`). The PC browser downloads the copies from it.


**Remote control:** the page has the same buttons as the app (**Detect disc**, **Copy / resume**,
**Pause / cancel**, **Retry failed**, **Scan disc**) and a **Settings** page. A button on the page
does exactly what the button on the console does, and is disabled when the console's one is. The
app has to be open on the Xbox: the console suspends apps that leave the screen. The settings can't
be changed while the app is busy or while the Settings dialog is open on the console.

| Request | What it does |
|---------|--------------|
| `GET /` | Main page (or the PIN page). Refreshes every 5 s. |
| `POST /login` | `pin=NNNN`; sets a cookie. |
| `GET /files/NAME.iso` (`.map`, `.sha256`, `.log`) | Download, with `Range` / `If-Range`. The ISO download follows a copy that is still running. |
| `POST /delete` | `name=NAME`: deletes `NAME.iso`, `.map` and `.sha256`. Refused while that disc is being copied. |
| `POST /action` | `do=detect`, `copy`, `retry`, `scan` or `cancel`: presses that button of the app. Refused when the button is disabled on the console. |
| `GET /settings`, `POST /settings` | The settings form, with the same fields and checks as the console's Settings dialog. |
| `GET /favicon.png`, `/favicon.ico` | The app icon. |

- **While copying:** the server waits until the requested sectors have been tried. If the copy
  stops, the download is interrupted on purpose. Resume the copy, then *Resume* the download in
  the browser.
- **Screen:** the app keeps the screen on while it copies and while a download runs, because the
  Xbox suspends apps that leave the screen.
- **Space:** the image is reserved at full size when the copy starts (about 4.4 GiB for a DVD-5,
  6.3 GiB for a DVD-9). Delete each copy after downloading it.
