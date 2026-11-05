<p align="center">
  <img src="xbox-app/xISOBurn/Assets/Banner.png" alt="xISOBurn" width="640">
</p>

# xISOBurn

An Xbox Series X app (UWP, Developer Mode) that reads the disc in the console's optical drive
sector by sector and sends the image over the local network to a receiver on your PC, which writes
an ISO file.

It's useful when your PC's DVD drive fails on a disc (SCSI errors such as
`MEDIUM ERROR / UNRECOVERED READ ERROR`): the Xbox drive becomes a second reader. The destination
is the PC, over the network, because files on USB storage are limited to about 2 GB in Developer Mode.

> **Current status: milestone 2.** The app rescues the disc into a local file on the Xbox
> (LocalState). It works in multiple resumable passes in the style of `ddrescue`: the easy data first,
> then the damaged areas, with a map of every sector and a SHA-256 of the final image. There's no
> network yet.

## Documentation

| Document | Contents |
|----------|----------|
| [docs/install.md](docs/install.md) | Build (GitHub Actions), Device Portal access, installing the app, downloading logs |
| [docs/spike.md](docs/spike.md) | Milestone 1: the feasibility test and its results on a real Series X |
| [docs/milestone-2.md](docs/milestone-2.md) | Milestone 2: results of the first full read, the multi-pass rescue, `.map` format, `config.json`, test plan |

## Repository layout

| Folder | Contents |
|--------|----------|
| `xbox-app/` | UWP project in C# (`xISOBurn.sln`) |
| `xbox-app/xISOBurn/Scsi/` | Drive access: `Windows.Devices.Custom` + SCSI pass-through |
| `xbox-app/xISOBurn/Core/` | Disc detection, multi-pass rescue, sector state map, settings |
| `xbox-app/tools/make-assets.ps1` | Builds the package images from `designer.png` |
| `docs/` | Notes and test results |
| `.github/workflows/build.yml` | Build and packaging on GitHub Actions |
| `pc-receiver/` | PC receiver (milestone 3) |

## Output format

The image is a raw, sector-by-sector copy (2048 bytes per sector), identical to a plain ISO. The app
does **not** decrypt CSS: it only copies what the drive returns. If a drive returns scrambled data
from an encrypted DVD, the image is only useful to someone who can decrypt it later on the PC (for
example with MakeMKV). Sectors that can't be read are filled with zeros and listed in the `.map` file
next to the image.

## How the drive is accessed

The technique comes from XBSX2 PR #11,
["UWP/CDVD: Add support for the Xbox disc drive"](https://github.com/XboxEmulationHub/XBSX2/pull/11)
(file `pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp`). On the console, `CreateFile("\\.\X:")` and the
`IOCTL_CDROM`/`IOCTL_DVD` requests are blocked. What works in Developer Mode is opening the CD-ROM
device interface as a `CustomDevice` and sending `IOCTL_SCSI_PASS_THROUGH`.

## Disclaimer

xISOBurn is an independent project and isn't affiliated with or endorsed by Microsoft. Xbox and the
Xbox logo are trademarks of the Microsoft group of companies. The DVD logo is a trademark of the DVD
Format/Logo Licensing Corporation. Only copy discs you own and are allowed to back up.
