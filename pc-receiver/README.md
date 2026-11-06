# xisoburn-receiver

The PC side of xISOBurn: it receives the disc image from the Xbox app over the local network and
writes the ISO. It's a single Python script using only the standard library. GitHub Actions also
packages it as `xisoburn-receiver.exe`, so nothing needs to be installed.

## Running it

Download the **`xisoburn-receiver-N`** artifact from the latest **build** run on GitHub (Actions tab).
Then open PowerShell in the folder where you extracted it:

```powershell
.\xisoburn-receiver.exe --out D:\DVDs\backup
```

- `--out FOLDER`: one ISO per disc, named after the disc (for example `D:\DVDs\backup\ACQUARIA.iso`).
- `--out FILE.iso`: always write to that exact file.
- `--port 8770`: TCP port (default 8770).
- `--token SECRET`: shared secret. If you leave it out, a token is generated on the first run and
  kept in `xisoburn-receiver.token` next to the output, so it stays the same across runs.

On start it prints the **address, port and token** to type into the Xbox app (**Settings**). Leave
the window open while copying. **Ctrl+C** stops it, and the progress is kept.

With Python 3.8+ installed, `python xisoburn_receiver.py --out ...` works too.

## Windows Firewall

The first time, Windows asks whether to allow the receiver on the network. Allow it on **private
networks**. If you missed the prompt, run this once in an administrator PowerShell:

```powershell
New-NetFirewallRule -DisplayName "xISOBurn receiver" -Direction Inbound -Protocol TCP -LocalPort 8770 -Action Allow -Profile Private
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
