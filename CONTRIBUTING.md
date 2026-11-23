# Contributing to Solstice

Thanks for helping make Solstice better. Bug reports, ideas and pull requests are all welcome.

- **Found a bug?** Open a [bug report](https://github.com/PriyanshuGeTRekT/solstice-pc-game-streaming-sunshine-moonlight/issues/new/choose).
- **Have an idea?** Open a [feature request](https://github.com/PriyanshuGeTRekT/solstice-pc-game-streaming-sunshine-moonlight/issues/new/choose). For bigger changes, please open an issue before you start coding so we can agree on the approach.
- **Found a security problem?** Please don't open an issue. See [SECURITY.md](SECURITY.md).
- **A problem inside Sunshine or Moonlight themselves** (encoding, decoding, input, the stream itself) belongs with those projects: [Sunshine](https://github.com/LizardByte/Sunshine/issues) or [Moonlight](https://github.com/moonlight-stream/moonlight-qt/issues).

## Requirements

- Windows 10 or 11, 64-bit.
- The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- The Microsoft Edge WebView2 Runtime, which ships with Windows 11.

## Building

```powershell
git clone https://github.com/PriyanshuGeTRekT/solstice-pc-game-streaming-sunshine-moonlight.git
cd solstice-pc-game-streaming-sunshine-moonlight
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` publishes both apps as self-contained builds into `dist\`: the PC app folder (with `client\SolsticePlay.exe` inside), the single-file `SolsticePlay.exe`, and the release zips. To install your build for your own account:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Source dist\Solstice -Launch
```

For day-to-day work, `dotnet build src\Solstice` and `dotnet build src\SolsticePlay` are enough.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1
```

The smoke test starts both apps headless in **demo mode** and checks every screen's data, the UI files and the access controls. Demo mode serves simulated data and never touches Sunshine, Moonlight, the network or your game library, so it is safe to run anywhere and is what CI runs.

## Working on the interface

Both apps are a native window around a local web page (WebView2). Run either app headless and open it in a browser, with the UI files served straight from the source folder so edits show on reload:

```powershell
src\Solstice\bin\Debug\net8.0-windows\win-x64\Solstice.exe --serve --demo home --port 47650 --token dev --webroot src\Solstice\wwwroot
```

Then open `http://127.0.0.1:47650/?t=dev`. Drop `--demo home` to work against your real Sunshine. Demo scenes are listed at the top of `src/Solstice/Demo.cs` and `src/SolsticePlay/Demo.cs`. Deep links (`#devices`, `#library`, `#tuning`, `#activity`; `#settings` in Solstice Play) open a page directly.

The screenshots, demo GIF and social image in `docs/` are rendered from demo mode, so they never contain anyone's PC name, address or library:

```powershell
powershell -ExecutionPolicy Bypass -File tools\render-screens.ps1
python tools\make-media.py
```

## Style

- Plain words in the interface. Many people setting up game streaming are not network experts.
- Solstice never modifies Sunshine or Moonlight. It uses Sunshine's local API and config, and Moonlight's command line and saved settings.
- Anything that needs administrator rights goes through one Windows prompt, with the script passed in memory, never from a user-writable file.
- No telemetry, accounts or third-party services.
