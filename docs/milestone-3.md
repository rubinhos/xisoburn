# Milestone 3: download through the browser

The Xbox app is the only thing needed. It copies the disc to the Xbox (LocalState) with the
multi-pass rescue from milestone 2, and serves a **web page**. The PC browser downloads the ISO from
that page, and the download is resumable if the network drops. Nothing is installed on the PC: no
receiver, no firewall rule (the browser only makes outgoing connections).

> History: a first version sent the image to a separate PC program (`xisoburn-receiver`). A spike
> then showed that Developer Mode lets the app accept connections from the local network: on ports
> 8770, 8080 and 50080, from a PC browser, with a fast 64 MiB test download. So the receiver was
> removed in favour of the page. It is still in the git history.

## Using it

1. Open xISOBurn on the Xbox. The top of the screen shows **"On the PC, open http://192.168.x.x:8770/
   PIN 1234"**.
2. Insert the disc → **Detect disc** → **Copy / resume**.
3. On the PC, open that address in the browser and type the **PIN**. The page shows:
   - **Now on the Xbox:** the disc, the progress of the copy, the status line and the free space on the Xbox;
   - **Copies on the Xbox:** every image in LocalState, with its size, state (*Copying now*,
     *Incomplete* or *Complete* with its SHA-256) and links to download the **ISO**, the **map**
     and the **sha256**, plus **Delete**;
   - the log files, and the recent log.
4. Click **ISO** to download. You can start **while the copy is still running**: the download
   follows the copy, sending sectors as soon as they have been read, and finishes when the copy does.
5. When it's done, use **Delete** on the page to free the space on the Xbox.

## Downloads

- **Resumable:** the server supports HTTP *Range* requests. If the network drops, use *Resume* in the
  browser's downloads list (Edge and Chrome), and it continues from where it stopped.
- **While copying:** before sending a range, the server waits until those sectors have been tried by
  the copy. If the copy is paused (B) or stops, the download is **interrupted on purpose**: the
  browser shows it as failed and keeps the partial file. Resume the copy on the Xbox, then *Resume*
  the download in the browser.
- **Damaged discs:** sectors that failed are zeros in the ISO and are listed in the map. If **Retry
  failed** later recovers some of them, download the ISO again to get the improved image.
- **Integrity:** when nothing is left to try, the app computes the SHA-256 of the image and shows it
  on screen and on the page (and in `NAME.sha256`). To check the downloaded file on the PC:
  `Get-FileHash .\NAME.iso -Algorithm SHA256` in PowerShell.

## Copy now, download later

You can leave the Xbox copying and download only at the end. Keep the app on screen during the copy
(the Xbox suspends apps that leave the screen); the app keeps the screen on while it copies. After
the copy, the console may dim and idle-off as usual. If it does, turn it on and open xISOBurn again:
the image stays on the Xbox and the page comes back. While an ISO download is running, the app keeps
the screen on again, so a late download is not cut by the console going idle.

## Space on the Xbox

The image is reserved at its full size when the copy starts (6.3 GiB for a DVD-9). The test DVD's
copy proved that LocalState accepts that. The page shows the free space. Delete each copy after
downloading it. A Blu-ray (25–50 GB) may not fit.

## PIN and settings

- The page asks for a 4-digit **PIN** shown at the top of the Xbox screen. It keeps others on the
  network from downloading or deleting your copies. It is generated on first launch. You can change
  it, generate a new one, or switch the PIN off under **Settings → Web page**. PIN changes apply at once.
- The **port** (default 8770) can be changed in Settings. It's the only setting that needs an **app
  restart**; the app shows a notice when it does.

New keys in `config.json`: `WebPort`, `WebPinRequired`, `WebPin`. The old receiver keys
(`Destination`, `ReceiverHost`, `ReceiverPort`, `ReceiverToken`) are dropped automatically.

## Web routes

| Request | What it does |
|---------|--------------|
| `GET /` | Main page (or the PIN page). Refreshes every 5 s. |
| `POST /login` | `pin=NNNN`; sets a cookie. |
| `GET /files/NAME.iso` (`.map`, `.sha256`, `.log`) | Download, with `Range`/`If-Range` support. The ISO follows a copy in progress. |
| `POST /delete` | `name=NAME`: deletes `NAME.iso/.map/.sha256` (refused while that disc is being copied). |

Plain HTTP on the local network, one request per connection.

## Test plan

1. Open the page from the PC, type the PIN, and check that **Detect disc** on the Xbox shows up on
   the page within a few seconds.
2. Copy a healthy disc and start the ISO download right away. It should follow the copy and end
   with it.
3. Pause the copy with B halfway: the download should fail in the browser. Resume the copy, then
   *Resume* the download: it should continue, not restart.
4. Compare `Get-FileHash` of the downloaded ISO with the SHA-256 on the page.
5. Delete the copy on the page and check that the free space goes up.
6. Change the port in Settings and check the "Restart needed" notice.
