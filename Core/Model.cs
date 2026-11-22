using GamerFarm.Plugins;

namespace GamerFarm.Addons.TradeRules.Core;

/// <summary>The item kinds a rule can name. These are Steam's community item kinds, as the app passes them to add-ons.</summary>
public enum SpareKind {
	Other,
	Card,
	FoilCard,
	Background,
	Emoticon,
	BoosterPack,
	Gems,
	GemSack
}

/// <summary>An item in an offer, reduced to what a rule can ask about. Nothing here is guessed by the plugin.</summary>
/// <param name="AssetId">Steam's asset id, used only to tell two stacks apart.</param>
/// <param name="AppId">The inventory app (753 for Steam's own community items).</param>
/// <param name="GameAppId">For a community item, the game it belongs to; 0 when Steam didn't say.</param>
/// <param name="Amount">How many of it; more than one for gems.</param>
/// <param name="Name">The name people see.</param>
/// <param name="Hash">The market hash name.</param>
/// <param name="Kind">The item's kind.</param>
/// <param name="Tradable">Whether Steam says it can be traded.</param>
/// <param name="Marketable">Whether Steam says it can be sold on the market.</param>
/// <param name="Described">Whether Steam described the item at all.</param>
/// <param name="GooType">Steam's gem type for the item; 0 when it has none.</param>
/// <param name="GooBorder">The gem value's border; 1 for a foil card.</param>
public sealed record RuleItem(ulong AssetId, uint AppId, uint GameAppId, uint Amount, string Name, string Hash, SpareKind Kind,
	bool Tradable, bool Marketable, bool Described, int GooType, int GooBorder) {
	/// <summary>The underlying game: the community item's game, or the item's own app for a non-community item.</summary>
	public uint Game => GameAppId != 0 ? GameAppId : AppId;

	/// <summary>How many copies count as items; an amount Steam left at 0 still counts as one.</summary>
	public int Count => (int) Math.Clamp(Amount == 0 ? 1u : Amount, 1u, int.MaxValue);

	/// <summary>Make one from the offer the app handed to the judge.</summary>
	public static RuleItem From(PluginTradeItem item) => new(
		item.AssetId, item.AppId, item.GameAppId, item.Amount, item.Name ?? "", item.MarketHashName ?? "", KindOf(item.Kind),
		item.Tradable, item.Marketable, item.Described, item.GooType, item.GooBorder);

	/// <summary>Steam's kind, as the plugin API names it.</summary>
	public static SpareKind KindOf(PluginTradeItemKind kind) => kind switch {
		PluginTradeItemKind.Card => SpareKind.Card,
		PluginTradeItemKind.FoilCard => SpareKind.FoilCard,
		PluginTradeItemKind.Background => SpareKind.Background,
		PluginTradeItemKind.Emoticon => SpareKind.Emoticon,
		PluginTradeItemKind.BoosterPack => SpareKind.BoosterPack,
		PluginTradeItemKind.Gems => SpareKind.Gems,
		PluginTradeItemKind.GemSack => SpareKind.GemSack,
		_ => SpareKind.Other
	};
}

/// <summary>A card set the app knows: its size, how many cards are held and (when the source has them) the card hashes.</summary>
public sealed record CardSetInfo(uint AppId, string Game, int SetSize, int Owned, IReadOnlyList<string> Cards) {
	/// <summary>A set whose size isn't known; rules must treat that as Unknown, not as zero.</summary>
	public static readonly CardSetInfo Unknown = new(0, "", 0, 0, []);
}

/// <summary>One market price: the cheapest listing in the currency's smallest unit, and when it was read.</summary>
/// <param name="Cents">The lowest listing, in hundredths of the currency (0: nobody sells it).</param>
/// <param name="Currency">The market currency's code. Compared with the account's wallet code, never converted silently.</param>
/// <param name="At">When the price was read (UTC).</param>
public sealed record MarketQuote(int Cents, string Currency, DateTime At) {
	/// <summary>Take the price the app holds. Null stays null; the currency code is kept so a mismatch can be caught.</summary>
	public static MarketQuote? From(PluginMarketPrice? price) =>
		price == null ? null : new MarketQuote((int) Math.Round(price.Price), price.Currency ?? "", price.At);
}

/// <summary>Who sent an offer: its SteamID64, and a local name when the sender is one of our accounts.</summary>
public sealed record OfferPartner(ulong SteamId, string Name);

/// <summary>One side of a rule's partner filter: names or SteamID64s. A deny always wins.</summary>
public sealed record Partners(IReadOnlyList<string> Allow, IReadOnlyList<string> Deny) {
	/// <summary>Anybody, with no filters.</summary>
	public static readonly Partners Anyone = new([], []);

	/// <summary>Whether this partner may be considered: named on allow (or allow is empty) and not on deny.</summary>
	public bool Allows(OfferPartner partner) {
		if (Deny.Any(d => Matches(d, partner))) {
			return false;
		}

		return (Allow.Count == 0) || Allow.Any(a => Matches(a, partner));
	}

	private static bool Matches(string rule, OfferPartner partner) => rule.Equals(partner.Name, StringComparison.OrdinalIgnoreCase)
		|| (ulong.TryParse(rule, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) && (id == partner.SteamId));
}

/// <summary>What a rule applies to: kinds, games, exact market names, foil or not, and item counts. Empty means any.</summary>
public sealed record Match(IReadOnlyList<SpareKind> Kinds, IReadOnlyList<uint> Apps, IReadOnlyList<string> Hashes, IReadOnlyList<string> Names,
	bool? Foil, int? MinItems, int? MaxItems) {
	/// <summary>No filters at all.</summary>
	public static readonly Match Any = new([], [], [], [], null, null, null);

	/// <summary>Whether one item belongs to this match.</summary>
	public bool Matches(RuleItem item) {
		if ((Kinds.Count > 0) && !Kinds.Contains(item.Kind)) {
			return false;
		}

		if ((Apps.Count > 0) && !Apps.Contains(item.Game)) {
			return false;
		}

		if ((Hashes.Count > 0) && !Hashes.Contains(item.Hash, StringComparer.Ordinal)) {
			return false;
		}

		if ((Names.Count > 0) && !Names.Contains(item.Name, StringComparer.Ordinal)) {
			return false;
		}

		return !((Foil is bool foil) && (item.Kind is SpareKind.Card or SpareKind.FoilCard) && ((item.Kind == SpareKind.FoilCard) != foil));
	}
}

/// <summary>What a gem-priced rule takes as payment: the kinds named, and whether every payment item must be one of them.</summary>
public sealed record PayIn(IReadOnlyList<SpareKind> Kinds, bool Only) {
	/// <summary>Any item with a gem value may pay.</summary>
	public static readonly PayIn Anything = new([], false);
}

/// <summary>One side of a foil swap: how many items, foil or not, and which.</summary>
public sealed record SwapSide(int Count, bool Foil, Match Match);

/// <summary>The kinds of rule the add-on can build. Each one is a typed, reviewable decision.</summary>
public static class RuleKind {
	public const string CardGems = "card-gems";
	public const string SetSizeGems = "set-size-gems";
	public const string ItemThreshold = "item-threshold";
	public const string BoosterValue = "booster-value";
	public const string FoilSwap = "foil-swap";
	public const string MarketProfit = "market-profit";
	public const string ValueRatio = "value-ratio";

	/// <summary>Any condition the typed kinds don't cover: a small, sandboxed formula over the offer.</summary>
	public const string Expression = "expression";

	/// <summary>Every kind the codec accepts.</summary>
	public static readonly IReadOnlyList<string> All = [CardGems, SetSizeGems, ItemThreshold, BoosterValue, FoilSwap, MarketProfit, ValueRatio, Expression];
}

/// <summary>
/// One custom trade rule. Fields that don't belong to its kind stay at their defaults and are never read; the codec only
/// writes what the kind uses.
/// </summary>
public sealed record Rule {
	/// <summary>Unique id, used in the log when a rule decides.</summary>
	public string Id { get; init; } = "";

	/// <summary>One of <see cref="RuleKind.All"/>.</summary>
	public string Kind { get; init; } = "";

	/// <summary>"accept" (default) or "decline".</summary>
	public string Action { get; init; } = "accept";

	/// <summary>Whether the rule is used at all.</summary>
	public bool Enabled { get; init; } = true;

	/// <summary>Lower runs first. A decline always wins over an accept.</summary>
	public int Priority { get; init; } = 100;

	/// <summary>Free text shown on the page and used as the reason in the log.</summary>
	public string Note { get; init; } = "";

	/// <summary>Partner filters.</summary>
	public Partners Partners { get; init; } = Partners.Anyone;

	/// <summary>Which items the rule applies to.</summary>
	public Match Match { get; init; } = Match.Any;

	/// <summary>Payment kinds, for gem-priced rules.</summary>
	public PayIn? PayIn { get; init; }

	// card-gems
	/// <summary>"gems" (Steam's GooValue) or "wallet" (market net, converted through a sack).</summary>
	public string Worth { get; init; } = "gems";

	/// <summary>"none", "gems" or "wallet"; used only when the primary value is missing.</summary>
	public string Fallback { get; init; } = "none";

	/// <summary>Whether all given items must belong to one game.</summary>
	public bool SameGame { get; init; }

	/// <summary>Extra margin, in percent.</summary>
	public double MarginPct { get; init; }

	/// <summary>Extra margin, in gems.</summary>
	public long MinMarginGems { get; init; }

	// set-size-gems
	/// <summary>"card" or "completeSet".</summary>
	public string Per { get; init; } = "card";

	/// <summary>Card-set size to price, in gems.</summary>
	public IReadOnlyDictionary<int, long> Table { get; init; } = new Dictionary<int, long>();

	/// <summary>Price of a card whose set size isn't in the table.</summary>
	public long? DefaultPerCard { get; init; }

	/// <summary>Flat price of a whole set whose size isn't in the table.</summary>
	public long? DefaultPerSet { get; init; }

	/// <summary>"unknown" (leave the offer) or "abstain" when a set isn't known.</summary>
	public string OnUnknownSet { get; init; } = "unknown";

	// item-threshold
	/// <summary>"giving" for a sale, "receiving" for a purchase.</summary>
	public string Target { get; init; } = "";

	/// <summary>"sell" or "buy".</summary>
	public string Mode { get; init; } = "";

	/// <summary>Item-threshold: "gems" or "wallet". Value-ratio reuses this field for its basis.</summary>
	public string Basis { get; init; } = "gems";

	/// <summary>Sell: least each. Buy: most each.</summary>
	public long MinEach { get; init; }

	/// <summary>Sell: extra total.</summary>
	public long MinTotal { get; init; }

	/// <summary>Buy: most each.</summary>
	public long MaxEach { get; init; }

	/// <summary>Buy: extra total.</summary>
	public long MaxTotal { get; init; }

	// booster-value
	/// <summary>"expected", "floor", "setSize" or "fixed".</summary>
	public string Value { get; init; } = "expected";

	/// <summary>"gems" or "wallet".</summary>
	public string ValueIn { get; init; } = "gems";

	/// <summary>Margin in the same unit as <see cref="ValueIn"/>.</summary>
	public long MinMargin { get; init; }

	/// <summary>Least recent sales a price needs. The plugin API does not expose a count; see README.</summary>
	public int MinSales { get; init; } = 3;

	/// <summary>Oldest price a rule will use, in hours.</summary>
	public int MaxAgeHours { get; init; } = 24;

	/// <summary>How far under the cheapest listing to list when valuing a sale.</summary>
	public double UndercutPct { get; init; }

	/// <summary>"threeCheapestDistinct" or "threeTimesCheapest".</summary>
	public string FloorMode { get; init; } = "threeCheapestDistinct";

	/// <summary>A fixed value, in gems.</summary>
	public int Gems { get; init; }

	// foil-swap
	/// <summary>What the account gives.</summary>
	public SwapSide? Give { get; init; }

	/// <summary>What the account receives.</summary>
	public SwapSide? Get { get; init; }

	/// <summary>"exact" or "atLeast".</summary>
	public string SwapMode { get; init; } = "exact";

	/// <summary>Allowed loss in percent, or null to skip the value check.</summary>
	public int? MaxLossPct { get; init; }

	// market-profit
	/// <summary>Least net profit, in currency cents.</summary>
	public long MinProfitCents { get; init; } = 1;

	/// <summary>Least profit as a percentage of what is given.</summary>
	public double MinProfitPct { get; init; }

	/// <summary>"conservative", "lowest" or "median".</summary>
	public string PriceMode { get; init; } = "conservative";

	/// <summary>"sack" (loose gems at their share of a sack) or "unknown".</summary>
	public string LooseGems { get; init; } = "sack";

	// value-ratio (its basis is the <see cref="Basis"/> field above)

	/// <summary>"receiving" or "giving".</summary>
	public string Left { get; init; } = "receiving";

	/// <summary>"receiving" or "giving".</summary>
	public string Right { get; init; } = "giving";

	/// <summary>"&gt;=", "&lt;=", "&gt;", "&lt;" or "==".</summary>
	public string Op { get; init; } = ">=";

	/// <summary>What the right side is multiplied by.</summary>
	public double Factor { get; init; } = 1;

	/// <summary>What is added to the scaled right side.</summary>
	public double Offset { get; init; }

	/// <summary>The condition of an <c>expression</c> rule.</summary>
	public string Expr { get; init; } = "";

	/// <summary>Whether this is a veto.</summary>
	public bool Declines => Action.Equals("decline", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A parsed rule set: the rules in decision order, and everything wrong with the file.</summary>
public sealed record RuleSet(IReadOnlyList<Rule> Rules, IReadOnlyList<string> Problems) {
	/// <summary>An empty set with no problems.</summary>
	public static readonly RuleSet Empty = new([], []);

	/// <summary>Whether any rule was loaded.</summary>
	public bool Any => Rules.Count > 0;
}
