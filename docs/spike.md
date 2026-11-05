# Milestone 1: feasibility test (spike)

The goal was to find out, on a real console, whether the Xbox Series X drive reads the DVD and
**what** it returns. The test only reads: it writes nothing to the disc or the drive and uses no
network. It is still available in the app as **Feasibility test**.

To build and install the app, see [install.md](install.md).

## Running the test

1. Insert the disc. If the console opens the movie player, close it.
2. Open xISOBurn, select **Feasibility test** and press **A**. Don't leave the app while it runs.
   The test takes under a minute if the disc is fine, and several minutes if there are bad sectors
   (each one can take up to 30 s).
3. A **SUMMARY** appears at the end. The full log, `spike-YYYYMMDD-HHMMSS.log`, and the first ~2 MB
   of the disc, `spike-first-1000-sectors.bin`, are saved in LocalState (see install.md, section 5).

## What the test answers

| # | Question | Where to look in the log |
|---|----------|--------------------------|
| 1 | Does enumeration find the CD-ROM interface, and does `CustomDevice` open in Dev Mode with only the two `DeviceCapability` entries from the XBSX2 PR? | "Looking for the drive", "Opening the drive" |
| 2 | Is the IOCTL code `0x0004D004`? | "IOCTL code" |
| 3 | Drive vendor, model and firmware | INQUIRY |
| 4 | Media type (DVD-ROM/CD/BD) | GET CONFIGURATION |
| 5 | Does the capacity (READ CAPACITY) match the size computed from the DVD structure? Is it **dual layer**? OTP or PTP? | READ CAPACITY, READ DVD STRUCTURE, "Size comparison" |
| 6 | Does the disc use CSS? Which regions is it for? | READ DVD STRUCTURE format 1 |
| 7 | Which region is the Xbox drive set to? | REPORT KEY format 8 |
| 8 | How many sectors per command work (16/31/32/48/64)? Does the data match? | "Transfer size test" |
| 9 | Do the first 1000 sectors read? Is it ISO9660/UDF? | "Reading the first", "File system" |
| 10 | **Does the drive return the video sectors without CSS authentication?** Does it refuse them (sense `05/6F/03`) or return them scrambled? | "Sample reads" and the SUMMARY |
| 11 | Do layer 1 and the layer change read? | "end of layer 0" / "start of layer 1" samples |
| 12 | Where do medium errors (`03/11/00` etc.) appear, and how long does each take? | lines containing "failed" |

How to read item 10:
- **Samples read and "scrambled: 0"**: either the disc has no CSS in those areas or the drive
  authenticated on its own. The ISO is usable as is.
- **Samples read with "scrambled" > 0**: the drive returns encrypted data. The ISO comes out
  scrambled and is only useful with later decryption on the PC (e.g. MakeMKV).
- **Samples refused with `05/6F/03`**: the drive requires CSS authentication before returning video
  sectors. Without it, all of the video would show up as "bad sectors".

## Results (2026-11-05, Xbox Series X, build 0.1.2.0, DVD "ACQUARIA")

There are two logs: `spike-20261105-121317.log` is a complete run, and `spike-20261105-121301.log`
was interrupted partway through the samples.

| # | Result |
|---|--------|
| 1 | **Works.** One interface, `\\?\SCSI#CdRom&Ven_PLDS&Prod_DG-6M5S#...`, named "XBOX". `CustomDevice` opens with only the two `DeviceCapability` entries from the PR. |
| 2 | IOCTL code `0x0004D004`, as expected. |
| 3 | Drive **PLDS DG-6M5S**, firmware **014R**. |
| 4 | Profile `0x0010` (DVD-ROM). |
| 5 | **DVD-9: 2 layers, OTP.** Physical start sector `0x030000`, end `0xF635C6`, end of layer 0 `0x1FB52F`. Layer 0 = 1,881,392 sectors, layer 1 = 1,436,407 sectors, **total 3,317,799 sectors (6,794,852,352 bytes)**, exactly the ISO9660 volume size. **READ CAPACITY returns 15,939,015**, which is `end − start + 1` ignoring OTP, so it is **wrong** for this disc. Asking for the layer 1 structure returns `05/24/00`, which is normal for OTP: only layer 0 has a descriptor. |
| 6 | **No protection (CPST = 0, no CSS).** Disc regions: 1 and 4. |
| 7 | Drive set to region 1 (RPC Phase II, 4 changes left). This doesn't matter for this disc: without CSS the drive doesn't enforce the region. |
| 8 | 16, 31, 32, 48 and **64** sectors per command all work, and the content matches 16-sector reads. |
| 9 | Sectors 0–999 read without errors. The disc uses ISO9660 ("ACQUARIA") + UDF (BEA01/NSR02/TEA01, AVDP at sector 256), the standard DVD-Video layout. The first VOB is at LBA 328. |
| 10 | No sector is scrambled and none is refused: the disc has no CSS, so the ISO is usable directly. |
| 11 | Reading crosses the layer change: the end of layer 0 (LBA 1,881,376) reads MPEG data, and the start of layer 1 (LBA 1,881,392) reads successfully. **To check:** the first 16 sectors of layer 1 came back all zeros in 0.7 ms. This may be normal padding between files or a drive quirk; the full read (milestone 2) will show whether the following sectors contain video. |
| 12 | No medium errors in the ranges read. The 25%, 50%, 75% and "end of disc" samples used the wrong capacity and landed **past the real end** (LBAs > 3,317,799). They failed with "short response (3 bytes)", without sense data, taking about 1.1–1.5 s each. |

### Consequences for milestone 2

- **The disc size comes from the DVD structure** (OTP calculation), cross-checked with ISO9660.
  READ CAPACITY is only a last resort (CD/BD).
- **Errors can arrive without sense data.** The failure past the end of the disc came back as a
  3-byte response with no sense. Reads must treat this as a normal failure and then try
  REQUEST SENSE to find out why.
- **Default block of 32 sectors (64 KiB)**, configurable up to 64.
- **Speed:** the spike's numbers (1–2.5 MiB/s) come from short reads while the disc was still
  spinning up, so they aren't a valid estimate. Milestone 2 measures sustained speed.
- **Leaving the app mid-run:** the first run stopped without any error in the log (the app was
  probably closed or suspended). Milestone 2 handles the B button so it doesn't close the app.
