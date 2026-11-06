<p align="center">
  <img src="xbox-app/xISOBurn/Assets/Banner.png" alt="xISOBurn" width="640">
</p>

# xISOBurn

An Xbox Series X app (UWP, Developer Mode; the Series S has no disc drive) that copies the disc in the console's optical drive to an
ISO image, sector by sector. It serves a web page from which your PC's browser downloads the image.
You only install the Xbox app; the PC needs nothing.

- **Damaged discs:** the copy runs in multiple resumable passes, in the style of ddrescue. The
  readable data is copied first, then the damaged areas. A map records every sector, and the
  SHA-256 is computed when nothing is left to read. When the PC's DVD drive fails on a disc
  (`MEDIUM ERROR / UNRECOVERED READ ERROR`), the Xbox drive becomes a second reader.
- **CSS-protected DVD-Video:** the app authenticates with the drive and decrypts the video while
  copying, so the ISO plays in any player. The disc has to be from the console's region.
- **Download from the browser:** the copy reaches the PC over the local network. You can start the
  download while the copy is running, and resume it if the network drops. A PIN keeps others on the
  network out. The network is needed because files on USB storage are limited to about 2 GB in
  Developer Mode.
- **Gamepad only:** every setting can be changed on screen.

## Quick start

1. **Build:** push to `master` (or run the **build** workflow by hand). GitHub Actions produces the
   `xISOBurn-xbox-1.0.N.0` artifact.
2. **Install:** put the console in Developer Mode, turn on the Device Portal, and install the `.msix`
   with its dependencies from the PC browser. Step by step: [docs/install.md](docs/install.md).
3. **Copy:** open xISOBurn and insert the disc. Choose **Detect disc**, then **Copy / resume**. **B**
   pauses, and **Copy / resume** continues later, even after the app was closed.
4. **Download:** on the PC, open the address shown at the top of the Xbox screen
   (`http://192.168.x.x:8770/`), type the PIN, and click **ISO**. Compare the SHA-256 on the page with
   `Get-FileHash .\NAME.iso -Algorithm SHA256`.
5. **Clean up:** **Delete** on the page frees the space on the Xbox.

Other buttons: **Retry failed** tries the bad sectors once more, **Scan disc** runs a quick read-only
health check, and **Settings** opens the settings.

## Documentation

| Document | Contents |
|----------|----------|
| [docs/install.md](docs/install.md) | Build on GitHub Actions, Developer Mode and the Device Portal, installing and updating, getting logs and files |
| [docs/how-it-works.md](docs/how-it-works.md) | Drive access, the copy passes, CSS decryption, the `.map` format, `config.json`, the web page |

## Repository layout

| Folder | Contents |
|--------|----------|
| `xbox-app/xISOBurn/Scsi/` | Drive access: `Windows.Devices.Custom` + SCSI pass-through |
| `xbox-app/xISOBurn/Core/` | Disc detection, multi-pass copy, sector map, settings, web server, disc scan |
| `xbox-app/xISOBurn/Css/` | CSS: C# port of libdvdcss (cipher, tables, drive commands, session) |
| `xbox-app/xISOBurn/Util/` | Logger and byte helpers |
| `xbox-app/tools/make-assets.ps1` | Builds the package images from `designer.png` |
| `.github/workflows/build.yml` | Build and packaging on GitHub Actions |

## Output

A raw image, 2048 bytes per sector, the same as a plain ISO. On CSS discs the video sectors are
decrypted. Sectors that can't be read stay zeros, and the `.map` file next to the image lists them.
The `.sha256` holds the hash of the finished image.

## References

### Code this project builds on

- **XBSX2 PR #11**, ["UWP/CDVD: Add support for the Xbox disc drive"](https://github.com/XboxEmulationHub/XBSX2/pull/11)
  ([XBSX2](https://github.com/XboxEmulationHub/XBSX2), `pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp` / `.h`).
  The drive access comes from there:
  - the AQS filter for the CD-ROM device interface;
  - opening it with `CustomDevice.FromIdAsync`;
  - the `IOCTL_SCSI_PASS_THROUGH` control code;
  - the buffered `SCSI_PASS_THROUGH` layout (header, sense and data in one buffer).

  The code in `Scsi/` is a C# rewrite of that technique, not a copy of the file. xISOBurn adds
  DATA OUT commands (MODE SELECT, SEND KEY), which XBSX2 doesn't send.
- **[libdvdcss](https://code.videolan.org/videolan/libdvdcss)** by VideoLAN (GPL-2.0-or-later),
  ported at commit `811ce97`. CSS decryption is a C# port of its code:
  - `Css/CssTables.cs`: generated verbatim from `src/csstables.h`;
  - `Css/CssCipher.cs`: `CryptKey`, `DecryptKey`, `DecryptDiscKey` with the player keys,
    `DecryptTitleKey` and `dvdcss_unscramble` from `src/css.c`, ported line by line;
  - `Css/CssSession.cs`: the authentication sequence of `GetBusKey`, `dvdcss_disckey` and
    `dvdcss_titlekey` (`src/css.c`);
  - `Css/CssDrive.cs`: the CDBs and response offsets of the raw-SCSI branches of `src/ioctl.c`.

  libdvdcss itself builds on css-auth by Derek Fawcus, the DVD CSS ioctls example by Andrew T.
  Veliath, Frank A. Stevenson's work on CSS, DeCSSPlus and DecVOB.
- **[GNU ddrescue](https://www.gnu.org/software/ddrescue/)**. The multi-pass copy follows its ideas:
  - a fast first pass that skips damaged areas;
  - a fill pass, then sector-by-sector scraping and retry passes;
  - a resumable map of sector states.

  The `.map` format is xISOBurn's own, and no ddrescue code is used.

### Specifications

| Topic | Reference |
|-------|-----------|
| SCSI commands: TEST UNIT READY, REQUEST SENSE, READ(10), READ CAPACITY, GET CONFIGURATION, READ DVD STRUCTURE (physical, copyright, disc key), REPORT KEY, SEND KEY, SET CD SPEED, MODE SENSE/SELECT page 01h | T10 [MMC (Multi-Media Commands)](https://www.t10.org/drafts.htm) and SPC (sense keys and ASC/ASCQ) |
| DVD physical format, layers, OTP addressing (the DVD-9 size calculation) | [ECMA-267](https://ecma-international.org/publications-and-standards/standards/ecma-267/) (DVD-ROM) |
| MPEG-2 program stream packs and the PES scrambling bits | ISO/IEC 13818-1 (MPEG-2 Systems) |
| ISO 9660 volume descriptor and directories (volume label, file list, VOB locations) | [ECMA-119](https://ecma-international.org/publications-and-standards/standards/ecma-119/) |
| UDF anchor at sector 256 | [ECMA-167](https://ecma-international.org/publications-and-standards/standards/ecma-167/) and the OSTA UDF specification |
| Resumable downloads (`Range`, `If-Range`, `206`, `416`, `ETag`) | [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110) (HTTP semantics) |

### Microsoft documentation

- [`Windows.Devices.Custom`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.custom):
  `CustomDevice`, `IOControlCode`.
- [`SCSI_PASS_THROUGH`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddscsi/ns-ntddscsi-_scsi_pass_through)
  (`ntddscsi.h`).
- [`StreamSocketListener`](https://learn.microsoft.com/en-us/uwp/api/windows.networking.sockets.streamsocketlistener):
  the web server inside the app.
- [`DisplayRequest`](https://learn.microsoft.com/en-us/uwp/api/windows.system.display.displayrequest):
  keeps the screen on during copies and downloads.
- [UWP on Xbox](https://learn.microsoft.com/en-us/windows/uwp/xbox-apps/) and the
  [Xbox Device Portal](https://learn.microsoft.com/en-us/windows/uwp/xbox-apps/device-portal-xbox).

### Build

- GitHub Actions: [`actions/checkout`](https://github.com/actions/checkout),
  [`microsoft/setup-msbuild`](https://github.com/microsoft/setup-msbuild),
  [`actions/upload-artifact`](https://github.com/actions/upload-artifact).

## License

xISOBurn is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License](LICENSE) as published by the Free Software Foundation, either version 2
of the License, or (at your option) any later version (`GPL-2.0-or-later`). It is distributed in the
hope that it will be useful, but WITHOUT ANY WARRANTY; see the license for details.

The license follows from the CSS code. The files in `xbox-app/xISOBurn/Css/` are derived from
libdvdcss (Copyright VideoLAN, GPL-2.0-or-later) and keep its copyright and license notices.

## Disclaimer

xISOBurn is an independent project and isn't affiliated with or endorsed by Microsoft. Xbox and the
Xbox logo are trademarks of the Microsoft group of companies. The DVD logo is a trademark of the DVD
Format/Logo Licensing Corporation. Only copy discs you own and are allowed to back up.
