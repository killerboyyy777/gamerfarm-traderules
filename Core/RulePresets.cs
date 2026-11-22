namespace GamerFarm.Addons.TradeRules.Core;

/// <summary>One ready-made rule, with the words shown on the page.</summary>
/// <param name="Id">Short id used by the page buttons.</param>
/// <param name="Label">What it says on the page.</param>
/// <param name="Rule">The rule itself.</param>
public sealed record Preset(string Id, string Label, Rule Rule);

/// <summary>The gallery the dashboard offers: one for each kind, plus the examples people ask for.</summary>
public static class RulePresets {
	/// <summary>All presets, in the order the page shows them.</summary>
	public static IReadOnlyList<Preset> All() => [
		new("spares-worth", "Spares for their gem worth", new Rule {
			Id = "spares-worth", Kind = RuleKind.CardGems,
			Note = "Give backgrounds or emoticons - the things spare items would turn into gems anyway - and take at least their GooValue in gems. Add Card to the match when you want cards included too.",
			Match = new([SpareKind.Background, SpareKind.Emoticon], [], [], [], null, null, null),
			Worth = "gems", Fallback = "wallet", SameGame = false, PayIn = new([SpareKind.Gems, SpareKind.GemSack], true)
		}),
		new("set-size-table", "Gems by card-set size", new Rule {
			Id = "set-size-table", Kind = RuleKind.SetSizeGems, Note = "A price table for each card of a set size.",
			Match = new([SpareKind.Card], [], [], [], false, null, null), Per = "card",
			Table = new Dictionary<int, long> { [5] = 15, [8] = 20, [10] = 25, [15] = 35 }
		}),
		new("sell-backgrounds", "Sell backgrounds for gems", new Rule {
			Id = "sell-backgrounds", Kind = RuleKind.ItemThreshold,
			Note = "Give backgrounds, take at least 100 gems each in gems or gem sacks. Edit the price to your own rate card.",
			Match = new([SpareKind.Background], [], [], [], null, null, null), Target = "giving", Mode = "sell", Basis = "gems",
			MinEach = 100, PayIn = new([SpareKind.Gems, SpareKind.GemSack], true)
		}),
		new("sell-boosters-expected", "Sell boosters at their expected pull", new Rule {
			Id = "sell-boosters-expected", Kind = RuleKind.BoosterValue, Note = "Give boosters, take at least the average of their 3 cards.",
			Match = new([SpareKind.BoosterPack], [], [], [], null, null, null), Target = "giving", Mode = "sell", Value = "expected", ValueIn = "gems", MarginPct = 10
		}),
		new("buy-boosters-floor", "Buy boosters below their worst pull", new Rule {
			Id = "buy-boosters-floor", Kind = RuleKind.BoosterValue, Note = "Take boosters, give no more than the three cheapest cards are worth.",
			Match = new([SpareKind.BoosterPack], [], [], [], null, null, null), Target = "receiving", Mode = "buy", Value = "floor", ValueIn = "gems",
			FloorMode = "threeCheapestDistinct"
		}),
		new("foils-for-normals", "Foils for normals", new Rule {
			Id = "foils-for-normals", Kind = RuleKind.FoilSwap,
			Note = "One foil for three normal cards of the same game, never at a loss - raise maxLossPct when you want to accept a loss on the foil.",
			Give = new(1, true, new([SpareKind.FoilCard], [], [], [], true, null, null)),
			Get = new(3, false, new([SpareKind.Card], [], [], [], false, null, null)),
			SameGame = true, SwapMode = "exact", MaxLossPct = 0
		}),
		new("expression-gems", "Any trade: a tenth more in gems", new Rule {
			Id = "expression-gems", Kind = RuleKind.Expression, Note = "Take at least a tenth more in gems than you give.",
			Expr = "receiving.gems >= giving.gems * 1.15", LooseGems = "sack"
		}),
		new("market-profit", "Any trade with market profit", new Rule {
			Id = "market-profit", Kind = RuleKind.MarketProfit, Note = "Take trades whose net market value is above what is given.",
			MinProfitCents = 1, MinProfitPct = 0, MinSales = 3, MaxAgeHours = 24, PriceMode = "conservative", LooseGems = "sack"
		}),
		new("exchange-gems", "Key/gem exchange: at least a tenth more in gems", new Rule {
			Id = "exchange-gems", Kind = RuleKind.ValueRatio,
			Note = "Take any trade that brings at least 10% more gems than it gives - the rule a key/gem exchange bot runs on. Edit the factor to your spread.",
			Basis = "gems", Left = "receiving", Op = ">=", Right = "giving", Factor = 1.1, LooseGems = "sack"
		}),
		new("levelup-sets", "Level-up: any whole set for gems", new Rule {
			Id = "levelup-sets", Kind = RuleKind.SetSizeGems,
			Note = "Give a complete card set of any size, take the same gem price for every set. Edit the price to your list.",
			Match = new([SpareKind.Card], [], [], [], false, null, null), Per = "completeSet",
			Table = new Dictionary<int, long>(), DefaultPerSet = 1000,
			PayIn = new([SpareKind.Gems, SpareKind.GemSack], true)
		}),
		new("ratio", "Compare the two sides", new Rule {
			Id = "ratio", Kind = RuleKind.ValueRatio, Note = "Take trades worth at least this much more than they give.",
			Basis = "wallet", Left = "receiving", Op = ">=", Right = "giving", Factor = 1.1, MinSales = 3, MaxAgeHours = 24, PriceMode = "conservative"
		})
	];

	/// <summary>One preset by id, or null.</summary>
	public static Preset? Find(string id) => All().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
