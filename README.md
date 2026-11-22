# Custom trade rules for gamer.farm

[![Release](https://img.shields.io/github/v/release/killerboyyy777/gamerfarm-traderules?logo=github&label=release)](https://github.com/killerboyyy777/gamerfarm-traderules/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/killerboyyy777/gamerfarm-traderules/total?logo=github&label=downloads)](https://github.com/killerboyyy777/gamerfarm-traderules/releases)
[![License](https://img.shields.io/github/license/killerboyyy777/gamerfarm-traderules)](LICENSE.txt)
[![gamer.farm](https://img.shields.io/badge/gamer.farm-2.0.3%2B-blue)](https://github.com/VisaHolder/gamerfarm)

Custom trade rules for **[gamer.farm](https://github.com/VisaHolder/gamerfarm)** 2.0.3+. The add-on judges incoming trade offers on the accounts you switch on, accepting or declining them by rules you write yourself.

This repository holds the source. The GitHub release ships **only** `gamerfarm.traderules.dll`, which is all gamer.farm needs — never `gamerFarm.dll` or any of the add-on's source.

> [!WARNING]
> **Trade automation can lose items.** A judge that accepts offers cannot know what you actually wanted to keep. Test new rules with small trades first, keep a `Decline` rule for items you never want to give away, and never run an untested rule set on an account holding items you cannot afford to lose.

## Features

- **Eight rule kinds**: `card-gems`, `set-size-gems`, `item-threshold`, `booster-value`, `foil-swap`, `market-profit`, `value-ratio` and `expression`, covering gem pricing, set sizes, market profit, foil swaps and custom formulas.
- **Expression language**: a small sandboxed formula language with side values (gems, wallet, counts, sets, cards, foils, boosters), `count`/`has`/`sum`/`min`/`max`/`avg`/`sameGame`, `&&`/`||`, arithmetic and comparisons.
- **Safe by default**: every account is off until you switch it on; if no rule matches, or a value is unknown, the offer is left alone. Unknown is never treated as zero.
- **Partner and item filters**: allow or deny partners (SteamID64), and match by item kind, app, hash, name, foil state and item count.
- **Decline vetoes**: decline rules win regardless of priority, so a rule that protects your rares cannot be overridden by an accepting rule.
- **Dashboard page and commands**: a **Trade rules** tab to load presets, paste rule JSON and preview it, plus a `traderules` command for previewing and editing settings from chat.
- **App-enforced limits**: the app keeps the final say on trade holds, tradability, kept items, promised items and confirmations. The add-on only answers offers the app leaves undecided.

## How it interacts with gamer.farm

gamer.farm calls this add-on's judge only for offers left undecided, and only when you have enabled add-on trade answers. The app:

- still applies its own trade-hold, untradable, keep-list and promise checks, and asks for confirmation as usual;
- re-asks the judge just before acting, so a rule decided on stale data is reconsidered;
- treats a `Leave` answer (or an unknown value, or disagreement between judges) as no answer, leaving the offer in the app's hands.

The add-on adds its own gates on top (trade hold, undescribed or untradable items, excluded partners) and caps each offer at 8 uncached value asks and 10 seconds.

## Installation

1. Download `gamerfarm.traderules.dll` from the [latest release](https://github.com/killerboyyy777/gamerfarm-traderules/releases).
2. Copy it into your gamer.farm `plugins/` folder (next to the executable, or the one named by `--path`).
3. In gamer.farm, turn **Load plugins** on in Settings and restart.
4. Open the **Trade rules** dashboard tab. Load a preset or paste rule JSON, preview it, then switch it on per account.

The add-on is **off for every account by default** and needs gamer.farm **2.0.3 or newer** (`GemValueAsync`, `CardSet` and the wallet properties were added in 2.0.3). On an older app it logs a message and does nothing.

## Rules

The JSON has `version: 1` and a `rules` array. Each rule has an `id`, a `kind`, an `action` (`accept` or `decline`), `enabled`, `priority`, an optional `note`, `when.partners`, `match` and `payIn`. All eight kinds from the original feature work: `card-gems`, `set-size-gems`, `item-threshold`, `booster-value`, `foil-swap`, `market-profit`, `value-ratio` and `expression`. The expression language (variables, `count`/`has`/`sum`/`min`/`max`/`avg`/`sameGame`, `&&`/`||`, arithmetic and comparisons) is the same one, including "Unknown is not zero — leave the offer".

Decline rules are vetoes and win regardless of priority; accepting rules run in priority order; a matched rule with an unknown value leaves the offer; no match leaves the app's ordinary behaviour in charge. A hard gate (trade hold, undescribed or untradable item, or a partner excluded for the account) stops the rules first. Each offer gets at most 8 uncached value asks and 10 seconds; the second pass only refreshes when a value was missing or stale.

Gems (Steam's GooValue) and wallet (market net after Steam's fee) stay separate bases. A rule says which it uses; conversions through a sack of 1,000 gems only happen where the rule asks for them (`worth`, `fallback`, `valueIn`, `looseGems`). Market prices are only used when their currency code matches the account's wallet code.

Settings: `Enabled`, `RulesJson`, `PartnersAllow` and `PartnersDeny` are per account. The `traderules` command explains the saved rules (`traderules preview <account> [json]`) and edits only settings (`on`, `off`, `preset`, `export`, `import`, `copy`); it never answers an offer. Page buttons call that command.

## What the plugin API cannot see

The plugin contract is deliberately narrow, so a few options from the original in-app feature are enforced as far as the API allows:

- `minSales` is parsed and shown in the preview, but the API exposes no sales count. Price freshness is checked with `maxAgeHours` instead.
- `priceMode` cannot distinguish lowest from median: the API carries one lowest listing. All three modes value from that price.
- Booster `expected` and `floor` need the hashes of the cards in the set. `CardSet` supplies the size and owned count but not the hashes, so those two modes return Unknown (the offer is left) with a real host. `setSize` and `fixed` work normally.
- Offer names for partner filters come from your own configured accounts; a stranger has only a SteamID64.
- There is no API to list waiting offers, so "preview" explains the rule set in plain words rather than replaying live offers. Actual verdicts and their reasons appear in the log and on the page under **Recent answers**.

## Configuration

Rules live in the plugin settings, per account: `Enabled`, `RulesJson`, `PartnersAllow` and `PartnersDeny`. The **Trade rules** tab edits the same settings as the `traderules` command:

```json
{
  "version": 1,
  "rules": [
    {
      "id": "spares-worth",
      "kind": "card-gems",
      "action": "accept",
      "enabled": true,
      "priority": 100,
      "note": "Take at least the gem value of backgrounds and emoticons you give away",
      "match": { "kinds": ["Background", "Emoticon"] },
      "payIn": { "kinds": ["Gems", "GemSack"], "only": true },
      "worth": "gems",
      "fallback": "wallet",
      "sameGame": false,
      "marginPct": 10,
      "minMarginGems": 0
    }
  ]
}
```

Decline rules are vetoes and win regardless of priority; accepting rules run in priority order. A matched rule whose value cannot be determined leaves the offer. See [Rules](#rules) above for every field and kind.

## Commands

The `traderules` command explains rules and edits settings; it never answers an offer.

| Command | Description |
| --- | --- |
| `traderules preview <account> [json]` | Explain the saved rules (or the given JSON) in plain words. |
| `traderules on <account>` / `traderules off <account>` | Switch the add-on on or off for one account. |
| `traderules preset <account> <name>` | Load a built-in preset. |
| `traderules export <account>` / `traderules import <account> <json>` | Copy rules out or in. |

## Logging

Every accepted or declined offer is logged with the offer ID and the rule that decided it, so a surprising answer can be traced back to a rule. Unknown values and budget limits are logged too.

## Building from source

The add-on compiles against the official `gamerFarm.dll` from a gamer.farm **2.0.3** release. That assembly is not committed here. Point the build at your copy:

```powershell
dotnet build TradeRules.csproj -c Release -p:GamerFarmDll="C:\path\to\gamerFarm.dll"
```

The output is `bin/Release/net10.0/gamerfarm.traderules.dll`. `<Private>false</Private>` keeps `gamerFarm.dll` out of the output — if it appears there, the reference is misconfigured. Requires the .NET 10 SDK.

## Layout

- Root — plugin source, `TradeRules.csproj`, and embedded `lang/de.json`.
- Releases — one asset: `gamerfarm.traderules.dll`.

`addons/addons.json` in gamer.farm is upstream-signed; this repository does not edit it. Add-on authors who want it listed in the official catalog must ask upstream to add the entry and the release DLL URL.

## Contact

- **Issues:** [github.com/killerboyyy777/gamerfarm-traderules/issues](https://github.com/killerboyyy777/gamerfarm-traderules/issues)
- **Steam:** [steamcommunity.com/id/klb777](https://steamcommunity.com/id/klb777)
- **Discord:** killerboyyy777

## License

Apache-2.0. See [LICENSE.txt](LICENSE.txt).
