# gamerfarm-traderules

Custom trade rules for [gamer.farm](https://github.com/VisaHolder/gamerfarm) 2.0.3+, as a single-DLL add-on.

This repository holds the source. The GitHub release ships **only** `gamerfarm.traderules.dll`, which is all gamer.farm needs — never `gamerFarm.dll` or any of the add-on's source.

## Install

1. Download `gamerfarm.traderules.dll` from the [latest release](https://github.com/killerboyyy777/gamerfarm-traderules/releases).
2. Copy it into your gamer.farm `plugins/` folder (next to the executable, or the one named by `--path`).
3. In gamer.farm, turn **Load plugins** on in Settings and restart.
4. Open the **Trade rules** dashboard tab. Load a preset or paste rule JSON, preview it, then switch it on per account.

The add-on is **off for every account by default** and needs gamer.farm **2.0.3 or newer** (`GemValueAsync`, `CardSet` and the wallet properties were added in 2.0.3). On an older app it logs a message and does nothing.

See [the add-on README](addons/traderules/README.md) for the rule JSON format, all eight rule kinds, the expression language, and what the plugin API cannot see.

## Build from source

The add-on compiles against the official `gamerFarm.dll` from a gamer.farm **2.0.3** release. That assembly is not committed here. Point the build at your copy:

```powershell
dotnet build addons/traderules/TradeRules.csproj -c Release -p:GamerFarmDll="C:\path\to\gamerFarm.dll"
```

The output is `addons/traderules/bin/Release/net10.0/gamerfarm.traderules.dll`. `<Private>false</Private>` keeps `gamerFarm.dll` out of the output — if it appears there, the reference is misconfigured. Requires the .NET 10 SDK.

## Layout

- `addons/traderules/` — plugin source, csproj, embedded `lang/de.json` and its own README.
- Releases — one asset: `gamerfarm.traderules.dll`.

`addons/addons.json` in gamer.farm is upstream-signed; this repository does not edit it. Add-on authors who want it listed in the official catalog must ask upstream to add the entry and the release DLL URL.
