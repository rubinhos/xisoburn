# xisoburn-receiver

The PC side of xISOBurn: it receives the disc image from the Xbox app over the local network and
writes the ISO. It's a single Python script using only the standard library. GitHub Actions also
packages it as `xisoburn-receiver.exe`, so nothing needs to be installed.

## Running it

Download the **`xisoburn-receiver-N`** artifact from the latest **build** run on GitHub (Actions
tab), unzip it and double-click **`xisoburn-receiver.exe`**. The exe isn't signed, so Windows
SmartScreen may warn the first time: choose **More info → Run anyway**.

The window has:

| Part | What it's for |
|------|---------------|
| **Settings** | Output folder (**Browse...**), port (default 8770) and token (**New token** makes another). They're remembered for next time in `%APPDATA%\xisoburn-receiver\settings.json`. |
| **Start / Stop** | Starts or stops receiving. Stopping is safe: the Xbox app keeps retrying for a while and can always resume later. |
| **Allow in Windows Firewall** | Creates the firewall rule for the port on private networks (Windows asks for administrator permission). |
| **Open output folder** | Opens the folder with the ISOs in Explorer. |
| **Type these on the Xbox** | The **PC address, port and token** to enter in xISOBurn → **Settings** on the Xbox. |
| **Discs** | Every disc received in this session: size received, %, speed, status (*Receiving*, *Paused*, *Finished* with the start of the SHA-256). |
| **Log** | What happened: discs created/resumed, progress every 5%, finished, and refused connections (usually a wrong token). |

Closing the window while a disc is arriving asks for confirmation first; the progress is kept either way.

### Command line (optional)

- `xisoburn-receiver.exe --out D:\DVDs\backup` opens the window with that folder and starts at once.
- `--port 8770` and `--token SECRET` pre-fill those fields.
- With Python 3.8+, `python xisoburn_receiver.py` opens the same window, and
  `python xisoburn_receiver.py --nogui --out D:\DVDs\backup` runs in the console. In console mode a
  token is generated on the first run and kept in `xisoburn-receiver.token` next to the output.
- `--out FILE.iso` (console mode) always writes to that exact file instead of one ISO per disc.

## Windows Firewall

The first time, Windows asks whether to allow the receiver on the network. Allow it on **private
networks**. If you missed the prompt, use **Allow in Windows Firewall** in the window, or run this
once in an administrator PowerShell:

```powershell
New-NetFirewallRule -DisplayName 'xISOBurn receiver (TCP 8770)' -Direction Inbound -Protocol TCP -LocalPort 8770 -Action Allow -Profile Private
```

Your PC's network must be set to **Private** (Settings → Network & internet → your connection →
Network profile type). The receiver also refuses any client that isn't on the local network
(private, loopback or link-local addresses).

## Files it writes (next to the ISO)

| File | Contents |
|------|----------|
| `NAME.iso` | The image, full disc size (sparse file). Sectors that were never received are zeros. |
| `NAME.iso.received.json` | Which sector ranges have arrived. This is what allows resuming. |
| `NAME.map` | The app's sector map: every sector read OK, failed, or not tried, with the last error. |
| `NAME.sha256` | SHA-256 of the finished ISO (`sha256sum` format). |
| `NAME.log` | What the receiver did with this disc: created, resumed, progress every 5%, finished. |

The receiver never overwrites an ISO it didn't create (one without a `.received.json` next to it).
Move such a file away, or set a different file name in the app.

## Tests

```powershell
cd pc-receiver
python -m unittest -v test_receiver
```
