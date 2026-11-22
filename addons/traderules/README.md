# Custom trade rules (add-on)

A working port of the deferred in-app "Custom trade rules" feature, rebuilt as a gamer.farm **2.0.3** add-on. It judges only the incoming offers the app leaves undecided, and only on accounts you switch on. The app keeps the final say: holds, tradability, keep lists, promised items and confirmation still go through its own checks.

## Install

1. Build `TradeRules.csproj` (Release) and copy `gamerfarm.traderules.dll` into gamer.farm's `plugins/` folder.
2. In gamer.farm, turn **Load plugins** on (dashboard → Settings → plugins) and restart.
3. Open the **Trade rules** dashboard tab. Load a preset or paste rule JSON under **Plugins → trade rules → Rules JSON**, preview it, and only then switch **Use custom trade rules** on for that account.

The add-on needs gamer.farm **2.0.3 or newer**: it uses `GemValueAsync`, `CardSet` and the wallet properties added in that version. On an older app it logs a message and does nothing.

It is **off for every account by default**. Nothing is accepted or declined until an account's switch is on and a rule matches.

## Rules

The JSON has `version: 1` and a `rules` array. Each rule has an `id`, a `kind`, an `action` (`accept` or `decline`), `enabled`, `priority`, an optional `note`, `when.partners`, `match` and `payIn`. All eight kinds from the original feature work: `card-gems`, `set-size-gems`, `item-threshold`, `booster-value`, `foil-swap`, `market-profit`, `value-ratio` and `expression`. The expression language (variables, `count`/`has`/`sum`/`min`/`max`/`avg`/`sameGame`, `&&`/`||`, arithmetic and comparisons) is the same one, including "Unknown is not zero - leave the offer".

Decline rules are vetoes and win regardless of priority; accepting rules run in priority order; a matched rule with an unknown value leaves the offer; no match leaves the app's ordinary behaviour in charge. A hard gate (trade hold, undescribed or untradable item, or a partner excluded for the account) stops the rules first. Each offer gets at most 8 uncached value asks and 10 seconds; the second pass only refreshes when a value was missing or stale.

Gems (Steam's GooValue) and wallet (market net after Steam's fee) stay separate bases. A rule says which it uses; conversions through a sack of 1,000 gems only happen where the rule asks for them (`worth`, `fallback`, `valueIn`, `looseGems`). Market prices are only used when their currency code matches the account's wallet code.

Settings: `Enabled`, `RulesJson`, `PartnersAllow` and `PartnersDeny` are per account. The `traderules` command explains the saved rules (`traderules preview <account> [json]`) and edits only settings (`on`, `off`, `preset`, `export`, `import`, `copy`); it never answers an offer. Page buttons call that command.

## What the plugin API cannot see

The plugin contract is deliberately narrow, so a few options from the original in-app feature are enforced as far as the API allows:

- `minSales` is parsed and shown in the preview, but the API exposes no sales count. Price freshness is checked with `maxAgeHours` instead.
- `priceMode` cannot distinguish lowest from median: the API carries one lowest listing. All three modes value from that price.
- Booster `expected` and `floor` need the hashes of the cards in the set. `CardSet` supplies the size and owned count but not the hashes, so those two modes return Unknown (the offer is left) with a real host. `setSize` and `fixed` work normally, as do all the tests' fake sources that provide card hashes.
- Offer names for partner filters come from your own configured accounts; a stranger has only a SteamID64.
- There is no API to list waiting offers, so "preview" explains the rule set in plain words rather than replaying live offers. Actual verdicts and their reasons appear in the log and on the page under **Recent answers**.
