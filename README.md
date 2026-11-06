<p align="center">
  <img src="xbox-app/xISOBurn/Assets/Banner.png" alt="xISOBurn" width="640">
</p>

# xISOBurn

An Xbox Series X app (UWP, Developer Mode) that reads the disc in the console's optical drive
sector by sector into an ISO image, and serves a web page from which your PC's browser downloads it.
The Xbox app is the only thing needed: nothing is installed on the PC.

It's useful when your PC's DVD drive fails on a disc (SCSI errors such as
`MEDIUM ERROR / UNRECOVERED READ ERROR`): the Xbox drive becomes a second reader. The copy reaches the PC
over the local network, because files on USB storage are limited to about 2 GB in Developer Mode.

> **Current status: milestone 3.** The app copies the disc to the Xbox in multiple resumable passes
> in the style of `ddrescue` (the easy data first, then the damaged areas), with a map of every
> sector and the SHA-256 of the final image. The PC browser downloads the ISO from the app's web page,
> even while the copy is running, and can resume if the network drops. Settings can be changed on screen.
>
> **Experimental: CSS test (proof of concept).** The **CSS test** button tries CSS authentication with
> the console's drive and descrambles a 4 MiB sample of the disc's first title VOB. It uses a C# port of
> [libdvdcss](https://code.videolan.org/videolan/libdvdcss). It doesn't change the copy yet. See
> [docs/css-poc.md](docs/css-poc.md).

## Documentation

| Document | Contents |
|----------|----------|
| [docs/install.md](docs/install.md) | Build (GitHub Actions), Device Portal access, installing the app, downloading logs |
| [docs/spike.md](docs/spike.md) | Milestone 1: the feasibility test and its results on a real Series X |
| [docs/milestone-2.md](docs/milestone-2.md) | Milestone 2: results of the first full read, the multi-pass rescue, `.map` format, `config.json`, test plan |
| [docs/milestone-3.md](docs/milestone-3.md) | Milestone 3: the web page, downloading (resumable, while copying), PIN, settings, test plan |
| [docs/css-poc.md](docs/css-poc.md) | CSS proof of concept: the libdvdcss port, what each step proves, how to run it and read the log |

## Repository layout

| Folder | Contents |
|--------|----------|
| `xbox-app/` | UWP project in C# (`xISOBurn.sln`) |
| `xbox-app/xISOBurn/Scsi/` | Drive access: `Windows.Devices.Custom` + SCSI pass-through |
| `xbox-app/xISOBurn/Core/` | Disc detection, multi-pass rescue, sector state map, settings, web server, CSS test |
| `xbox-app/xISOBurn/Css/` | C# port of libdvdcss: CSS cipher, tables, and the REPORT KEY / SEND KEY commands |
| `xbox-app/tools/make-assets.ps1` | Builds the package images from `designer.png` |
| `docs/` | Notes and test results |
| `.github/workflows/build.yml` | Build and packaging on GitHub Actions |

## Output format

The image is a raw, sector-by-sector copy (2048 bytes per sector), identical to a plain ISO. The app
does **not** decrypt CSS and does not authenticate with the drive: it only copies what the drive
returns. Sectors that can't be read are filled with zeros and listed in the `.map` file next to the
image. The experimental **CSS test** is the exception: it authenticates and saves a descrambled
sample as a separate `css-test-*.vob` file, never inside the image (see
[docs/css-poc.md](docs/css-poc.md)).

**CSS-protected discs** (most commercial DVD-Video): the console's drive refuses to return the
protected video sectors. Those reads fail at once (tens of milliseconds, with no error detail),
unlike real damage, which costs seconds per read. xISOBurn is meant for discs without CSS:
home-made DVDs, data and software discs, and unprotected video.
- **Before copying:** if Detect disc reports `Protection: CSS/CPPM`, **Copy / resume** asks for
  confirmation first.
- **During the copy:** quick refusals are counted (`refused N` in the progress). After a few of them,
  the screen and the log explain that this is protection or a region mismatch, not disc damage.

## How the drive is accessed

The technique comes from XBSX2 PR #11,
["UWP/CDVD: Add support for the Xbox disc drive"](https://github.com/XboxEmulationHub/XBSX2/pull/11)
(file `pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp`). On the console, `CreateFile("\\.\X:")` and the
`IOCTL_CDROM`/`IOCTL_DVD` requests are blocked. What works in Developer Mode is opening the CD-ROM
device interface as a `CustomDevice` and sending `IOCTL_SCSI_PASS_THROUGH`.

## References

### Code this project builds on

- **XBSX2 PR #11**, ["UWP/CDVD: Add support for the Xbox disc drive"](https://github.com/XboxEmulationHub/XBSX2/pull/11)
  (`pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp` / `.h`). Where xISOBurn's drive access comes from:
  - the AQS filter for the CD-ROM device interface;
  - opening it with `CustomDevice.FromIdAsync`;
  - the `IOCTL_SCSI_PASS_THROUGH` control code;
  - the buffered `SCSI_PASS_THROUGH` layout (header, sense and data in one buffer).

  The code in `Scsi/` is a C# rewrite of that technique, not a copy of the file.
- **[GNU ddrescue](https://www.gnu.org/software/ddrescue/)**. The multi-pass rescue strategy follows
  its ideas:
  - a fast first pass that skips damaged areas;
  - a fill pass, then sector-by-sector scraping and retry passes;
  - a resumable map of sector states.

  The `.map` format is xISOBurn's own, and no ddrescue code is used.
- **[libdvdcss](https://code.videolan.org/videolan/libdvdcss)** by VideoLAN
  (GPL-2.0-or-later). The CSS proof of concept
  is a C# port of parts of it. Unlike the XBSX2 item above, this one is a port of the code, not just
  the technique:
  - `Css/CssTables.cs`: generated verbatim from `src/csstables.h`;
  - `Css/CssCipher.cs`: `CryptKey`, `DecryptKey`, `DecryptDiscKey` with the player keys,
    `DecryptTitleKey` and `dvdcss_unscramble` from `src/css.c`, ported line by line;
  - `Core/CssTest.cs`: the authentication sequence of `GetBusKey`, `dvdcss_disckey` and
    `dvdcss_titlekey` (`src/css.c`);
  - `Css/CssDrive.cs`: the CDBs and response offsets of the raw-SCSI branches of `src/ioctl.c`.

  libdvdcss itself builds on css-auth by Derek Fawcus, the DVD CSS ioctls example by Andrew T.
  Veliath, Frank A. Stevenson's work on CSS, DeCSSPlus and DecVOB. On the Xbox, the commands travel
  through the XBSX2 pass-through above. Those files keep libdvdcss's GPL-2.0-or-later license.

### Specifications

| Topic | Reference |
|-------|-----------|
| SCSI commands: TEST UNIT READY, INQUIRY, READ(10), READ CAPACITY, GET CONFIGURATION, READ DVD STRUCTURE, REPORT KEY, SEND KEY, READ TOC, SET CD SPEED, MODE SENSE/SELECT page 01h, REQUEST SENSE | T10 [MMC (Multi-Media Commands)](https://www.t10.org/drafts.htm) and SPC (sense keys and ASC/ASCQ) |
| DVD physical format, layers, OTP addressing (the DVD-9 size calculation) | [ECMA-267](https://ecma-international.org/publications-and-standards/standards/ecma-267/) (DVD-ROM) |
| ISO 9660 volume descriptor and directories (volume label, file list) | [ECMA-119](https://ecma-international.org/publications-and-standards/standards/ecma-119/) |
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

## Disclaimer

xISOBurn is an independent project and isn't affiliated with or endorsed by Microsoft. Xbox and the
Xbox logo are trademarks of the Microsoft group of companies. The DVD logo is a trademark of the DVD
Format/Logo Licensing Corporation. Only copy discs you own and are allowed to back up.
