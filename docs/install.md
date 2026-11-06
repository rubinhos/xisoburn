# Building and installing xISOBurn on the Xbox

You don't need to install anything on your PC. GitHub Actions builds the app, and the Xbox Device
Portal (a web page served by the console) installs it.

## 1. Get the package

**Easiest: GitHub Releases.** Every build of `master` is published as a release (`v1.0.N.0`) on the
repository's **Releases** page, with exactly the files the Device Portal needs:
- `xISOBurn_1.0.N.0_x64.msix`: the app;
- `Dependencies-x64.zip`: the three `.appx` dependencies (the .NET Native runtime and VCLibs). Unzip it first;
- `xISOBurn.cer`: the public part of the signing certificate.

**Or build it yourself** on GitHub Actions:

1. Push to `master`, or start the workflow by hand: **Actions** tab → **build** → **Run workflow**.
2. Wait for the **Xbox app (UWP x64)** job to finish. It takes 5–10 minutes because the .NET Native
   compilation is slow.
3. On the run page, download the **`xISOBurn-xbox-1.0.N.0`** artifact and unzip it. You only need:
   ```
   package\xISOBurn_1.0.N.0_x64_Test\
       xISOBurn_1.0.N.0_x64.msix                  (the app)
       Dependencies\x64\*.appx                    (.NET Native runtime and VCLibs, 3 files)
   ```
   You can ignore everything else: the `arm64`/`x86` dependencies, the PC install scripts and the
   telemetry files.

The package is signed with a self-signed certificate (`CN=xISOBurn`). The public part is in
`xISOBurn.cer`, but you shouldn't need it on the Xbox.

To build locally instead (optional), install Visual Studio 2022 with the *Universal Windows Platform
development* workload, open `xbox-app/xISOBurn.sln` and choose `Release | x64`.

## 2. Turn on the Device Portal (on the Xbox)

1. Start the Xbox in **Developer Mode**. The home screen becomes **Dev Home** instead of the
   regular dashboard.
   - If Developer Mode isn't activated yet: install the *Xbox Dev Mode* app from the console's
     store, register the console with your Partner Center account, and restart into Developer Mode
     from that app.
2. In Dev Home, on the **Home** tab, find the **Remote Access** box and select **Remote Access Settings**.
3. Check **Enable Xbox Device Portal**.
4. Under **Authentication**, set a **user name and password**. You'll use them in the browser.
5. Back in Dev Home, the **Remote Access** box shows the address, for example `https://192.168.0.42:11443`.

## 3. Open the Device Portal (on the PC)

1. The PC must be on the **same network** as the Xbox (wired or Wi-Fi).
2. Open Edge or Chrome and go to the address from step 2. Include `https://` and the `:11443` port.
3. The browser warns "**Your connection isn't private**". This is expected: the console uses its own
   certificate. Click **Advanced** → **Continue to 192.168.x.x (unsafe)**.
4. Sign in with the user name and password you set on the console.

If it doesn't open:
- Make sure you used `https://` (not `http://`) and the `:11443` port.
- Make sure the Xbox is in Developer Mode (Dev Home on screen) and the Device Portal option is checked.
- Run `ping 192.168.x.x` in PowerShell. If it doesn't answer, the PC and the Xbox may be on
  different networks (guest Wi-Fi, client isolation on the router).
- The Xbox IP can change after a router restart. Check it again in Dev Home.

## 4. Install the app

1. On the **Home** page, in the **My games & apps** box, click **Add**.
2. Choose `xISOBurn_1.0.N.0_x64.msix` and click **Next**.
3. Under dependencies, add **all three** files from `Dependencies\x64\` and click **Start**.
4. The app appears in the Dev Home list as "xISOBurn".

**Updating:** while no fixed signing certificate is configured, each build is signed with a different
certificate. If installing a newer version complains about the signature, first remove the old one:
in **My games & apps**, click the three dots next to xISOBurn → **Remove**. Removing the app also deletes
its LocalState, including the copies stored on the Xbox: download them first.

To sign every build with the same certificate, create a `.pfx` with `Subject = CN=xISOBurn` and add
two repository secrets: `SIGNING_PFX_BASE64` (the file encoded in base64) and
`SIGNING_PFX_PASSWORD`.

## 5. Get logs and output files

In the Device Portal, open **File explorer** (left menu) → **LocalAppData** → `xISOBurn_<id>` →
**LocalState**. Click a file to download it. Use **Upload** to send an edited `config.json` back.

| File | What it is |
|------|------------|
| `xisoburn-YYYYMMDD-HHMMSS.log` | Log of one app session (detection and copy) |
| `config.json` | Settings (see [how-it-works.md](how-it-works.md#configjson)) |
| `<VOLUME>.iso` / `.map` / `.sha256` | The copy, the map of unreadable sectors and the hash |
