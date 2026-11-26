# xISOBurn engine

The copy engine shared by the two xISOBurn apps:

- the Xbox app, in this repository (`xbox-app/`, UWP, .NET Native, C# 7.3);
- the Windows app, [rubinhos/xisoburn-pc](https://github.com/rubinhos/xisoburn-pc) (WinForms, .NET 10),
  which includes this repository as a git submodule and compiles this folder.

| Folder | Contents |
|--------|----------|
| `Core/` | Disc detection, multi-pass rescue (`RescueJob`), sector map, disc scan, ISO 9660 file list |
| `Css/` | CSS: C# port of libdvdcss |
| `Bluray/` | AACS and BD+: C# ports of libaacs and libbdplus, UDF reader, key folder |
| `Scsi/` | SCSI commands, results, sense data, MMC profile names |
| `Util/` | Logger and byte helpers |

## Rules

- **Builds on both platforms.** No WinRT (`Windows.*`) and nothing newer than C# 7.3 (no `using var`,
  no switch expressions, no nullable annotations) and no API newer than .NET Standard 2.0, because the
  Xbox app compiles this code with .NET Native.
- **Change it once.** A fix here reaches both apps. After merging to `master`, update the submodule
  in xisoburn-pc (`git submodule update --remote xisoburn`) and commit the new pointer.

## What each app provides

Each app compiles these files next to the engine, with the same names and members:

| Type | Members the engine uses |
|------|-------------------------|
| `xISOBurn.Scsi.OpticalDrive` | `SectorSize`, `MaxTransferBytes`, `DeviceId`, `SendAsync`, `SendOutAsync`, `ReadSectorsAsync`, `EjectAsync` (null when the disc went out, else why not) |
| `xISOBurn.Core.CopyStore` | `OpenAsync(outputFolder, appFolder, subfolder)` (subfolder: an audio CD's own folder, which an app may ignore), `OpenWriteAsync`, `OpenReadAsync`, `SizeAsync`, `DeleteAsync`, `ReplaceAsync`, `FreeBytesAsync`, `ReadTextAsync`, `WriteTextAtomicAsync`, `Description`, `DisplayPath`, `LimitedTo4GiB`, `FormatBytes` |
| `xISOBurn.Core.CopyConfig` | the copy settings: `SectorsPerRead`, `TimeoutSeconds`, `SkipOnErrors`, `RetryPasses`, `RetryReadSpeedKBps`, `DriveReadRetryCount`, `StartSector`, `SectorLimit`, `WriteImage`, `OutputName`, `OutputFolder`, `DecryptCss`, `DecryptBluray`, `MarkBlurayDecrypted`, `EjectWhenDone` |
| `xISOBurn.Platform` | `AddKeysHint`, `ResetDriveHint`, `OtherDestinationHint`: the words that differ between the apps |
