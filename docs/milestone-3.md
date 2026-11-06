# Milestone 3: sending the image to the PC

The Xbox app reads the disc with the same multi-pass rescue as milestone 2. Instead of writing to
LocalState, it sends every block to **xisoburn-receiver** on your PC, which writes the ISO. This
avoids both the Xbox's internal storage and the ~2 GB file limit on USB storage in Developer Mode.

## Quick start

1. **PC:** download the `xisoburn-receiver-N` artifact (see [install.md](install.md)) and run
   `.\xisoburn-receiver.exe --out D:\DVDs\backup`. Allow it on private networks when Windows asks.
   Note the **address, port and token** it prints. Details: [pc-receiver/README.md](../pc-receiver/README.md).
2. **Xbox:** open xISOBurn → **Settings**. Set Destination = *PC receiver*, enter the address, port
   and token, and use **Test connection**. It should say "OK: the receiver saves to ...". Then **Save**.
3. Insert the disc → **Detect disc** → **Rescue / resume**. On a healthy disc pass 1 reads
   everything (~20 minutes for a DVD-9). The receiver then computes the SHA-256 of the ISO, and the
   app shows it.

## Settings screen

Every key of `config.json` can be changed on screen (**Settings**) with the controller: the D-pad
moves between fields, A opens the on-screen keyboard on a text field, B closes without saving.
**No restart is ever needed**: the settings are read again at the start of every run. The dialog
can't be opened while a copy is running. Invalid values (for example a port outside 1–65535) are
pointed out, and the dialog stays open until they are fixed.

New keys in `config.json`:

| Key | Meaning |
|-----|---------|
| `Destination` | `"pc"` (default) sends to the receiver; `"local"` writes to LocalState like milestone 2. |
| `ReceiverHost` | IP address (or name) of the PC. |
| `ReceiverPort` | Port, default 8770. |
| `ReceiverToken` | The token printed by the receiver. |

## How the transfer works

- Each block is read from the disc and immediately sent with `PUT /chunk`. The request carries
  `Content-Range` (its position in the disc) and `X-Content-SHA256` (the hash of the bytes, computed
  right after the read). The receiver checks the hash before writing. A block is marked as read in
  the app's map **only after the PC confirms it**.
- Network hiccups (Wi-Fi drop, receiver restarted) are retried with growing pauses for about
  2 minutes, without re-reading the disc. If the PC stays unreachable, the run stops with a message.
  Everything already confirmed is kept, and **Rescue / resume** continues later. Permanent errors
  (wrong token, a different disc with the same file name) stop immediately.
- At the start of every run the app asks the receiver which sectors it already has (`GET /status`).
  If the map on the Xbox says a sector was read but the PC doesn't have it (ISO deleted or moved,
  other output folder), that sector is read again.
- At the end of every run the app sends its map (`POST /finish`). The PC then has `NAME.map` next to
  the ISO, even after a pause. When nothing is left to try, the receiver also computes the
  **SHA-256 of the whole ISO**, and the app stores it in its own map.

### Integrity (difference from the original plan)

The original plan was to compare a SHA-256 computed by the app while reading with the PC's. Since
milestone 2 the reading happens in several passes, out of order and possibly across several runs,
so the app never sees the image as one continuous stream. Integrity is now checked in two places:
1. **Per block:** each block's SHA-256 is computed on the Xbox right after the read and verified by
   the receiver before writing. A block damaged on the way is rejected and sent again.
2. **Whole image:** the receiver computes the SHA-256 of the final ISO. Use it to check later copies
   of the file.

## Protocol

HTTP/1.1, JSON answers, header `X-Token` on every request; only local-network clients are accepted.

| Request | What it does |
|---------|--------------|
| `GET /hello` | Receiver info (used by *Test connection*). |
| `GET /status?name=N&total_sectors=T` | `{"exists", "received": [[start, end), ...], "received_sectors"}` |
| `PUT /chunk?name=N&total_sectors=T` | Body = sectors; `Content-Range: bytes a-b/size`; `X-Content-SHA256`. |
| `POST /finish?name=N&total_sectors=T&complete=0\|1` | Body = the map. Writes `N.map`; with `complete=1` also hashes the ISO. |

Errors: 400 invalid request or hash mismatch, 401 wrong token, 403 not on the local network,
409 an existing ISO belongs to another disc or wasn't created by the receiver.

## Test plan

1. Start the receiver on the PC and check that it prints the address and token.
2. In the app's Settings, run *Test connection*: once with the right token (OK), once with a wrong
   one ("the token is wrong").
3. Copy a healthy disc. Watch `NAME.log` on the PC (progress every 5%) and compare the speed with
   the local copy in milestone 2.
4. During the copy, stop the receiver (Ctrl+C) for 10 s and start it again: the app should log
   "sending ... failed; retrying" and carry on.
5. Pause with **B** and resume: the receiver should log "resuming ... N sectors already received".
6. At the end, check that the app shows the same SHA-256 as `NAME.sha256` on the PC, and that the
   ISO opens in Windows (double-click mounts it).
