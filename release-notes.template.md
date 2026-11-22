Custom trade rules for gamer.farm {{GAMERFARM_VERSION}}.

<!-- gamerfarm-version: {{GAMERFARM_VERSION}} -->
This add-on was built against the official gamer.farm {{GAMERFARM_VERSION}} `gamerFarm.dll`. It is refreshed automatically whenever gamer.farm ships a new release.

## Install

1. Download `gamerfarm.traderules.dll` from this release (it is the only asset).
2. Copy it into your gamer.farm `plugins/` folder.
3. Turn **Load plugins** on in gamer.farm Settings and restart.
4. In the **Trade rules** tab, switch the add-on on per account and load a preset or paste rule JSON.

The add-on is off by default. Requires gamer.farm {{GAMERFARM_VERSION}} or newer for `GemValueAsync`, `CardSet` and the wallet properties. Never copy `gamerFarm.dll` yourself - gamer.farm supplies the one it loaded.

## Asset

`gamerfarm.traderules.dll` - SHA-256 `{{SHA256}}`

## Features

- Eight rule kinds: `card-gems`, `set-size-gems`, `item-threshold`, `booster-value`, `foil-swap`, `market-profit`, `value-ratio`, `expression`.
- Small sandboxed expression language; partner and item filters; decline rules are vetoes; unknown value leaves the offer alone.
- Dashboard page and `traderules` command. gamer.farm still enforces trade holds, tradability, kept items and confirmations.

## Caveats

- The plugin API exposes no sales count or median price, so `minSales` is cosmetic and every `priceMode` uses the lowest listing.
- `CardSet` gives set size but no card hashes, so booster `expected`/`floor` are Unknown with a real host (`setSize`/`fixed` work).
- There is no API to list waiting offers, so preview explains the rule set rather than live offers.
- `addons/addons.json` is upstream-signed; this repository does not edit it. Ask upstream to add the catalog entry.

> [!WARNING]
> Trade automation can lose items. Test new rules with small trades first, keep a decline rule for items you never want to give away, and never run an untested rule set on an account holding items you cannot afford to lose.

Full source, configuration and docs: https://github.com/killerboyyy777/gamerfarm-traderules
