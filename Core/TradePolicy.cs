namespace GamerFarm.Addons.TradeRules.Core;

/// <summary>What a rule, or the whole rule set, says about an offer.</summary>
public enum TradeVerdict {
	/// <summary>No rule applies; the app's ordinary behaviour continues.</summary>
	Abstain,

	/// <summary>A rule accepted the offer.</summary>
	Accept,

	/// <summary>A rule vetoed the offer.</summary>
	Decline,

	/// <summary>A rule needed a value that isn't known; leave the offer.</summary>
	Unknown,

	/// <summary>A gate stopped the rules before they ran; leave the offer.</summary>
	Blocked
}

/// <summary>A rule's own answer, with the reason in plain words.</summary>
public sealed record RuleHit(string RuleId, TradeVerdict Verdict, string Why);

/// <summary>The whole answer: the verdict, the rule that decided it, why, and every rule that was tried.</summary>
public sealed record TradeDecision(TradeVerdict Verdict, string? RuleId, string Why, IReadOnlyList<RuleHit> Hits) {
	/// <summary>Whether the app must leave this offer alone.</summary>
	public bool Leaves => Verdict is TradeVerdict.Unknown or TradeVerdict.Blocked;
}

/// <summary>What the hard gates say before any rule runs.</summary>
public enum GateBlock {
	None,
	Blocked,
	Unknown
}

/// <summary>A gate's answer and why.</summary>
public sealed record GateResult(GateBlock Block, string Why) {
	/// <summary>Nothing in the way.</summary>
	public static readonly GateResult Allowed = new(GateBlock.None, "");

	/// <summary>Leave the offer; giving something the app wouldn't let go.</summary>
	public static GateResult Blocked(string why) => new(GateBlock.Blocked, why);

	/// <summary>Leave the offer; Steam's answer about it can't be trusted.</summary>
	public static GateResult Unknown(string why) => new(GateBlock.Unknown, why);
}

/// <summary>An offer as the gates see it, before any valuation.</summary>
public sealed record OfferShape(OfferPartner Partner, IReadOnlyList<RuleItem> Giving, IReadOnlyList<RuleItem> Receiving, int? HoldDays);

/// <summary>
/// The gates no custom rule may overrule: a trade hold, an undescribed or untradable item, and a partner the operator
/// excluded for the account. The app separately checks keep lists, promised items and holds before it acts on an Accept.
/// </summary>
public static class RuleGates {
	/// <summary>Check one offer. A Blocked or Unknown result stops the rules and leaves the offer for the owner.</summary>
	public static GateResult Check(OfferShape offer, IReadOnlyList<string> allow, IReadOnlyList<string> deny) {
		if (offer.HoldDays is > 0) {
			return GateResult.Blocked("the trade would be held");
		}

		if (!new Partners(allow, deny).Allows(offer.Partner)) {
			return GateResult.Blocked("the sender isn't on this account's allowed list, or is on its denied list");
		}

		foreach (RuleItem item in offer.Giving) {
			if (!item.Described || !item.Tradable) {
				return GateResult.Blocked($"{Shown(item)} can't be traded (Steam's trade hold, or it isn't tradable)");
			}
		}

		foreach (RuleItem item in offer.Receiving) {
			if (!item.Described || !item.Tradable) {
				return GateResult.Unknown($"{Shown(item)} isn't tradable - Steam's answer about the offer can't be trusted");
			}
		}

		return GateResult.Allowed;
	}

	private static string Shown(RuleItem item) => item.Name.Length > 0 ? item.Name : item.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Runs the custom rules in order. Decline vetoes win first, then the first rule that accepts; an Unknown leaves the offer
/// for the next look, and no rule at all is Abstain.
/// </summary>
public static class TradePolicy {
	/// <summary>Decide synchronously, for a caller that has no cancellation to pass.</summary>
	public static TradeDecision Decide(RuleSet set, TradeValues values, GateResult gates) =>
		DecideAsync(set, values, gates, CancellationToken.None).GetAwaiter().GetResult();

	/// <summary>Decide one offer.</summary>
	public static async Task<TradeDecision> DecideAsync(RuleSet set, TradeValues values, GateResult gates, CancellationToken ct) {
		if (gates.Block != GateBlock.None) {
			return new TradeDecision(gates.Block == GateBlock.Blocked ? TradeVerdict.Blocked : TradeVerdict.Unknown, null, gates.Why, []);
		}

		if (!set.Any) {
			return new TradeDecision(TradeVerdict.Abstain, null, "no custom rule applies", []);
		}

		List<RuleHit> hits = [];
		RuleHit? unknown = null;

		// Shape and partner vetoes first: a matching decline always wins.
		foreach (Rule rule in set.Rules.Where(static r => r.Declines)) {
			if (!rule.Enabled) {
				continue;
			}

			RuleHit veto = await VetoAsync(rule, values, ct).ConfigureAwait(false);

			if (veto.Verdict == TradeVerdict.Decline) {
				return new TradeDecision(TradeVerdict.Decline, rule.Id, veto.Why, [.. hits, veto]);
			}

			if ((veto.Verdict == TradeVerdict.Unknown) && (unknown == null)) {
				unknown = veto;
			}

			hits.Add(veto);
		}

		foreach (Rule rule in set.Rules.Where(static r => !r.Declines)) {
			if (!rule.Enabled) {
				continue;
			}

			RuleHit hit;

			try {
				(TradeVerdict verdict, string why) = await EvaluateAsync(rule, values, ct).ConfigureAwait(false);
				hit = new RuleHit(rule.Id, verdict, why);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				hit = new RuleHit(rule.Id, TradeVerdict.Unknown, $"couldn't weigh the offer: {e.GetType().Name}: {e.Message}");
			}

			hits.Add(hit);

			if (hit.Verdict == TradeVerdict.Accept) {
				return new TradeDecision(TradeVerdict.Accept, rule.Id, hit.Why, hits);
			}

			if ((hit.Verdict == TradeVerdict.Unknown) && (unknown == null)) {
				unknown = hit;
			}
		}

		return unknown is { } u
			? new TradeDecision(TradeVerdict.Unknown, u.RuleId, u.Why, hits)
			: new TradeDecision(TradeVerdict.Abstain, null, "no custom rule applies", hits);
	}

	/// <summary>
	/// Plain words for the preview: the order rules run in, what each one needs, and why a decline still wins. Acts on
	/// nothing; no price is asked for.
	/// </summary>
	public static IReadOnlyList<string> Explain(RuleSet set) {
		List<string> lines = [];

		if (set.Problems.Count > 0) {
			lines.Add("Problems in the saved JSON:");
			lines.AddRange(set.Problems.Select(p => "  " + p));
		}

		if (!set.Any) {
			lines.Add("No rules loaded - the app's ordinary behaviour decides offers.");
			return lines;
		}

		lines.Add("Hard gates first: a held offer, an untradable or undescribed item, or a partner excluded for the account leaves the offer alone.");
		lines.Add("A decline rule that matches wins whatever its priority; then the first accepting rule in priority order. Unknown leaves the offer; no match leaves the app's behaviour in charge.");

		foreach (Rule rule in set.Rules) {
			string parts = $"  {rule.Priority,5}  {(rule.Enabled ? rule.Action : "disabled"),-7}  {rule.Id,-18}  {rule.Kind}";

			if (rule.Note.Length > 0) {
				parts += "  - " + rule.Note;
			}

			lines.Add(parts);
			lines.Add("        " + Describe(rule));
		}

		return lines;
	}

	private static string Describe(Rule r) => r.Kind switch {
		RuleKind.CardGems => $"match {Side(r.Match)}; worth {r.Worth}, fallback {r.Fallback}, margin {r.MarginPct}% + {r.MinMarginGems} gems; payment {Pay(r.PayIn)}",
		RuleKind.SetSizeGems => $"match {Side(r.Match)}; {r.Per}, table {r.Table.Count} entry(ies), default card {r.DefaultPerCard?.ToString() ?? "none"}, default set {r.DefaultPerSet?.ToString() ?? "none"}, unknown set: {r.OnUnknownSet}",
		RuleKind.ItemThreshold => $"{r.Mode} {r.Target} {Side(r.Match)}; {r.Basis}, " + (r.Mode == "sell" ? $"at least {r.MinEach} each + {r.MinTotal}" : $"at most {r.MaxEach} each + {r.MaxTotal}"),
		RuleKind.BoosterValue => $"{r.Mode} {r.Target} {Side(r.Match)}; {r.Value} in {r.ValueIn}, margin {r.MarginPct}% + {r.MinMargin}, floor {r.FloorMode}",
		RuleKind.FoilSwap => $"give {r.Give?.Count ?? 0} (foil {r.Give?.Foil}) for {r.Get?.Count ?? 0} (foil {r.Get?.Foil}), {r.SwapMode}, max loss {r.MaxLossPct?.ToString() ?? "no value check"}%",
		RuleKind.MarketProfit => $"net market profit at least {r.MinProfitCents} cent(s) and {r.MinProfitPct}%, price {r.PriceMode}, loose gems {r.LooseGems}",
		RuleKind.Expression => r.Expr,
		_ => $"{r.Left} {r.Op} {r.Right} * {r.Factor} + {r.Offset}, basis {r.Basis}"
	};

	private static string Side(Match m) => m.Kinds.Count == 0 ? "any items" : string.Join("/", m.Kinds);

	private static string Pay(PayIn? p) => p == null ? "any gem-valued item" : $"{string.Join("/", p.Kinds)} (only: {p.Only})";

	/// <summary>A decline rule is a shape and partner veto, or an expression that declines when it is true.</summary>
	private static async Task<RuleHit> VetoAsync(Rule rule, TradeValues values, CancellationToken ct) {
		if (!rule.Partners.Allows(values.Partner)) {
			return new RuleHit(rule.Id, TradeVerdict.Abstain, "from somebody this rule doesn't name");
		}

		if (rule.Kind == RuleKind.Expression) {
			(double? value, string why) = await ExpressionValueAsync(rule, values, ct).ConfigureAwait(false);

			return value is not double yes ? new RuleHit(rule.Id, TradeVerdict.Unknown, why)
				: yes != 0 ? new RuleHit(rule.Id, TradeVerdict.Decline, Reason(rule, "the offer matches a rule that declines"))
				: new RuleHit(rule.Id, TradeVerdict.Abstain, "the offer doesn't match");
		}

		return Shape(rule, values)
			? new RuleHit(rule.Id, TradeVerdict.Decline, Reason(rule, "the offer matches a rule that declines"))
			: new RuleHit(rule.Id, TradeVerdict.Abstain, "the offer doesn't match");
	}

	private static string Reason(Rule rule, string fallback) => rule.Note.Length > 0 ? rule.Note : fallback;

	private static async Task<(TradeVerdict, string)> EvaluateAsync(Rule rule, TradeValues values, CancellationToken ct) {
		if (!rule.Partners.Allows(values.Partner)) {
			return (TradeVerdict.Abstain, "from somebody this rule doesn't name");
		}

		return rule.Kind switch {
			RuleKind.CardGems => await CardGemsAsync(rule, values, ct).ConfigureAwait(false),
			RuleKind.SetSizeGems => await SetSizeGemsAsync(rule, values, ct).ConfigureAwait(false),
			RuleKind.ItemThreshold => await ItemThresholdAsync(rule, values, ct).ConfigureAwait(false),
			RuleKind.BoosterValue => await BoosterValueAsync(rule, values, ct).ConfigureAwait(false),
			RuleKind.FoilSwap => await FoilSwapAsync(rule, values, ct).ConfigureAwait(false),
			RuleKind.MarketProfit => await MarketProfitAsync(rule, values, ct).ConfigureAwait(false),
			RuleKind.Expression => await ExpressionAsync(rule, values, ct).ConfigureAwait(false),
			_ => await ValueRatioAsync(rule, values, ct).ConfigureAwait(false)
		};
	}

	private static async Task<(TradeVerdict, string)> ExpressionAsync(Rule r, TradeValues v, CancellationToken ct) {
		(double? value, string why) = await ExpressionValueAsync(r, v, ct).ConfigureAwait(false);

		return value is not double yes ? (TradeVerdict.Unknown, why)
			: yes != 0 ? (TradeVerdict.Accept, Reason(r, "a custom expression accepts it"))
			: (TradeVerdict.Abstain, "the expression isn't met");
	}

	private static async Task<(double? Value, string Why)> ExpressionValueAsync(Rule r, TradeValues v, CancellationToken ct) {
		if (!TradeExpression.TryParse(r.Expr, out TradeExpression? expr, out string? problem) || (expr == null)) {
			return (null, problem ?? "the expression isn't valid");
		}

		TradeExpressionContext context = new(v, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems);
		double? value = await expr.EvaluateAsync(context, ct).ConfigureAwait(false);

		return value is null ? (null, "the expression needs a value that isn't known") : (value, "");
	}

	// -- the rules -------------------------------------------------------------
	private static async Task<(TradeVerdict, string)> CardGemsAsync(Rule r, TradeValues v, CancellationToken ct) {
		if ((v.Giving.Count == 0) || !AllMatch(v.Giving, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely items this rule matches");
		}

		if (r.SameGame && (v.Giving.Select(static i => i.Game).Distinct().Count() > 1)) {
			return (TradeVerdict.Abstain, "the cards are from more than one game");
		}

		if (!Pays(r, v.Receiving)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		long worth = 0;

		foreach (RuleItem item in v.Giving) {
			(int? gems, string? problem) = await CardWorthAsync(r, item, v, ct).ConfigureAwait(false);

			if (gems is not int g) {
				return (TradeVerdict.Unknown, problem ?? "an item's worth isn't known");
			}

			worth += (long) g * item.Count;
		}

		long required = Required(worth, r.MarginPct, r.MinMarginGems);
		long? payment = await PaymentAsync(r, v, ct).ConfigureAwait(false);

		return Compare(payment, required, "gems", "the items are worth");
	}

	private static async Task<(TradeVerdict, string)> SetSizeGemsAsync(Rule r, TradeValues v, CancellationToken ct) {
		if ((v.Giving.Count == 0) || !AllMatch(v.Giving, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely cards this rule matches");
		}

		if (!Pays(r, v.Receiving)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		long required = 0;

		if (r.Per == "completeSet") {
			foreach (IGrouping<uint, RuleItem> set in v.Giving.GroupBy(static i => i.Game)) {
				int? size = await SizeAsync(set.First(), v, ct).ConfigureAwait(false);
				long? per = Table(r, size);

				if ((size is null) || (per is null)) {
					return UnknownSet(r, "a card's set size isn't known");
				}

				if (set.Sum(static i => i.Count) != size.Value) {
					return (TradeVerdict.Abstain, "the offer isn't a whole set");
				}

				required += per.Value;
			}
		} else {
			foreach (RuleItem item in v.Giving) {
				int? size = await SizeAsync(item, v, ct).ConfigureAwait(false);
				long? per = Table(r, size);

				if (per is null) {
					return UnknownSet(r, "a card's set size isn't known, and the table has no default");
				}

				required += per.Value * item.Count;
			}
		}

		required = Required(required, r.MarginPct, r.MinMarginGems);
		long? payment = await PaymentAsync(r, v, ct).ConfigureAwait(false);

		return Compare(payment, required, "gems", "the cards' table price");
	}

	private static (TradeVerdict, string) UnknownSet(Rule r, string why) =>
		r.OnUnknownSet == "abstain" ? (TradeVerdict.Abstain, why) : (TradeVerdict.Unknown, why);

	private static async Task<(TradeVerdict, string)> ItemThresholdAsync(Rule r, TradeValues v, CancellationToken ct) {
		IReadOnlyList<RuleItem> target = r.Target == "giving" ? v.Giving : v.Receiving;
		IReadOnlyList<RuleItem> paymentSide = r.Target == "giving" ? v.Receiving : v.Giving;

		if ((target.Count == 0) || !AllMatch(target, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely items this rule matches");
		}

		if (!Pays(r, paymentSide)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		int count = target.Sum(static i => i.Count);
		long? payment = await ValueAsync(r, paymentSide, r.Target != "giving", r.Basis, v, ct).ConfigureAwait(false);

		return r.Mode == "sell"
			? Compare(payment, (r.MinEach * count) + r.MinTotal, r.Basis, "the items are worth at least")
			: CompareAtMost(payment, (r.MaxEach * count) + r.MaxTotal, r.Basis, "the most it may give");
	}

	private static async Task<(TradeVerdict, string)> BoosterValueAsync(Rule r, TradeValues v, CancellationToken ct) {
		IReadOnlyList<RuleItem> target = r.Target == "giving" ? v.Giving : v.Receiving;
		IReadOnlyList<RuleItem> paymentSide = r.Target == "giving" ? v.Receiving : v.Giving;

		if ((target.Count == 0) || !AllMatch(target, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely booster packs");
		}

		if (!Pays(r, paymentSide)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		long value = 0;

		foreach (IGrouping<uint, RuleItem> game in target.GroupBy(static i => i.Game)) {
			long? each = await PackWorthAsync(r, game.Key, v, ct).ConfigureAwait(false);

			if (each is not long worth) {
				return (TradeVerdict.Unknown, "a booster pack's worth isn't known");
			}

			value += worth * game.Sum(static i => i.Count);
		}

		long? payment = await ValueAsync(r, paymentSide, r.Target != "giving", r.ValueIn, v, ct).ConfigureAwait(false);

		return r.Mode == "sell"
			? Compare(payment, (long) Math.Ceiling(value * (1 + (r.MarginPct / 100.0))) + r.MinMargin, r.ValueIn, "the packs are worth")
			: CompareAtMost(payment, Math.Max(0, (long) Math.Floor(value * (1 - (r.MarginPct / 100.0))) - r.MinMargin), r.ValueIn, "the most it may give for the packs");
	}

	private static async Task<(TradeVerdict, string)> FoilSwapAsync(Rule r, TradeValues v, CancellationToken ct) {
		if (r.Give is not { } give || r.Get is not { } get) {
			return (TradeVerdict.Abstain, "the rule has no shape");
		}

		if (!SideMatches(v.Giving, give) || !SideMatches(v.Receiving, get)) {
			return (TradeVerdict.Abstain, "the offer isn't that many cards for that many foils");
		}

		if (r.SameGame && (v.Giving.Concat(v.Receiving).Select(static i => i.Game).Distinct().Count() > 1)) {
			return (TradeVerdict.Abstain, "the cards are from more than one game");
		}

		if (r.MaxLossPct is not int loss) {
			return (TradeVerdict.Accept, "the shape matches");
		}

		long? given = await v.WalletOfAsync(v.Giving, true, 1, 24 * 30, 0, "unknown", ct).ConfigureAwait(false);
		long? got = await v.WalletOfAsync(v.Receiving, false, 1, 24 * 30, 0, "unknown", ct).ConfigureAwait(false);

		if (given is not long g || got is not long f) {
			return (TradeVerdict.Unknown, "a card's market value isn't known");
		}

		return (f * 100) >= (g * (100 - Math.Clamp(loss, 0, 100)))
			? (TradeVerdict.Accept, "the foils are worth close enough to the normals")
			: (TradeVerdict.Decline, "the foils are worth too much less than the normals");
	}

	private static async Task<(TradeVerdict, string)> MarketProfitAsync(Rule r, TradeValues v, CancellationToken ct) {
		if ((r.LooseGems != "sack") && v.Giving.Concat(v.Receiving).Any(static i => i.Kind == SpareKind.Gems)) {
			return (TradeVerdict.Unknown, "loose gems have no value to go by (looseGems is \"unknown\", or there's no sack price)");
		}

		long? give = await v.WalletOfAsync(v.Giving, true, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct).ConfigureAwait(false);
		long? receive = await v.WalletOfAsync(v.Receiving, false, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct).ConfigureAwait(false);

		if (give is not long g) {
			return (TradeVerdict.Unknown, "what is given isn't priced well enough");
		}

		if (receive is not long got) {
			return (TradeVerdict.Unknown, "what is received isn't priced well enough");
		}

		if (g == 0) {
			return (TradeVerdict.Abstain, "nothing is given - a donation, not a market trade");
		}

		long profit = got - g;

		if (profit < r.MinProfitCents) {
			return (TradeVerdict.Abstain, $"the profit is below {r.MinProfitCents} cent(s)");
		}

		return (r.MinProfitPct <= 0) || (profit * 100 >= g * r.MinProfitPct)
			? (TradeVerdict.Accept, $"it makes {profit} cent(s) after Steam's fee")
			: (TradeVerdict.Abstain, $"the profit is below {r.MinProfitPct:0.#}% of what is given");
	}

	private static async Task<(TradeVerdict, string)> ValueRatioAsync(Rule r, TradeValues v, CancellationToken ct) {
		double? left = await RatioSideAsync(r, r.Left, v, ct).ConfigureAwait(false);

		if (left is not double l) {
			return (TradeVerdict.Unknown, "the receiving side can't be valued");
		}

		double? right = await RatioSideAsync(r, r.Right, v, ct).ConfigureAwait(false);

		if (right is not double w) {
			return (TradeVerdict.Unknown, "the giving side can't be valued");
		}

		double target = (w * r.Factor) + r.Offset;
		bool passes = r.Op switch {
			">=" => l >= target,
			"<=" => l <= target,
			">" => l > target,
			"<" => l < target,
			_ => Math.Abs(l - target) < 0.005
		};

		return passes
			? (TradeVerdict.Accept, $"the {r.Left} side is {l:0.##} against {target:0.##}")
			: (TradeVerdict.Abstain, $"the {r.Left} side is {l:0.##}, not {r.Op} {target:0.##}");
	}

	private static async Task<double?> RatioSideAsync(Rule r, string side, TradeValues v, CancellationToken ct) {
		IReadOnlyList<RuleItem> items = side == "giving" ? v.Giving : v.Receiving;

		switch (r.Basis) {
			case "gems":
				return await v.GemsOfAsync(items, ct).ConfigureAwait(false);
			case "wallet":
				return await v.WalletOfAsync(items, giving: side == "giving", r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct).ConfigureAwait(false);
			case "cardCount":
				return items.Where(static i => i.Kind is SpareKind.Card or SpareKind.FoilCard).Sum(static i => i.Count);
			case "foilCount":
				return items.Where(static i => i.Kind == SpareKind.FoilCard).Sum(static i => i.Count);
			case "itemCount":
				return items.Sum(static i => i.Count);
			default:
				double total = 0;

				foreach (RuleItem item in items) {
					int? size = await SizeAsync(item, v, ct).ConfigureAwait(false);

					if (size is not int s) {
						return null;
					}

					total += s * item.Count;
				}

				return total;
		}
	}

	// -- shared bits -----------------------------------------------------------
	private static bool Shape(Rule rule, TradeValues values) {
		if (rule.Kind == RuleKind.FoilSwap) {
			return rule.Give is { } g && rule.Get is { } t && SideMatches(values.Giving, g) && SideMatches(values.Receiving, t);
		}

		return AllMatch(values.Giving, rule.Match) && AllMatch(values.Receiving, rule.Match);
	}

	private static bool SideMatches(IReadOnlyList<RuleItem> side, SwapSide want) {
		if (!AllMatch(side, want.Match)) {
			return false;
		}

		if (side.Any(i => (i.Kind is SpareKind.Card or SpareKind.FoilCard) && ((i.Kind == SpareKind.FoilCard) != want.Foil))) {
			return false;
		}

		return side.Sum(static i => i.Count) >= want.Count;
	}

	private static bool AllMatch(IReadOnlyList<RuleItem> side, Match match) {
		int count = side.Sum(static i => i.Count);

		if ((match.MinItems is int min) && (count < min)) {
			return false;
		}

		if ((match.MaxItems is int max) && (count > max)) {
			return false;
		}

		return side.All(match.Matches);
	}

	private static bool Pays(Rule r, IReadOnlyList<RuleItem> side) =>
		r.PayIn is not { Only: true } pay || side.All(i => pay.Kinds.Contains(i.Kind));

	private static Task<long?> PaymentAsync(Rule r, TradeValues v, CancellationToken ct) =>
		(r.Worth == "wallet") || (r.Basis == "wallet") || (r.ValueIn == "wallet")
			? v.WalletOfAsync(v.Receiving, false, r.MinSales, r.MaxAgeHours, r.UndercutPct, "unknown", ct)
			: v.GemsOfAsync(v.Receiving, ct);

	private static Task<long?> ValueAsync(Rule r, IReadOnlyList<RuleItem> side, bool giving, string basis, TradeValues v, CancellationToken ct) =>
		basis == "wallet"
			? v.WalletOfAsync(side, giving, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct)
			: v.GemsOfAsync(side, ct);

	private static async Task<int?> SizeAsync(RuleItem item, TradeValues v, CancellationToken ct) =>
		(await v.SetAsync(item, ct).ConfigureAwait(false)).SetSize;

	private static long? Table(Rule r, int? size) {
		long? fallback = r.Per == "completeSet" ? r.DefaultPerSet : r.DefaultPerCard;

		return (size is int s) && r.Table.TryGetValue(s, out long per) ? per : fallback;
	}

	private static long Required(long worth, double marginPct, long minMargin) =>
		(long) Math.Ceiling(worth * (1 + (Math.Max(0, marginPct) / 100.0))) + Math.Max(0, minMargin);

	private static async Task<(int? Gems, string? Problem)> CardWorthAsync(Rule r, RuleItem item, TradeValues v, CancellationToken ct) {
		ValuedItem gems = await v.GemsAsync(item, ct).ConfigureAwait(false);
		ValuedItem wallet = await v.WalletAsync(item, true, r.MinSales, r.MaxAgeHours, r.UndercutPct, "unknown", ct).ConfigureAwait(false);
		long sack = await v.SackAsync(ct).ConfigureAwait(false) ?? 0;
		int? fromWallet = (wallet.WalletCents is int cents) && (sack > 0) ? (int) ((long) cents * 1000 / sack) : null;

		if (r.Worth == "wallet") {
			return fromWallet is int w ? (w, null)
				: r.Fallback == "gems" && gems.Gems is int g ? (g, null)
				: (null, wallet.Problem ?? "the card's market value can't be turned into gems");
		}

		return gems.Gems is int value ? (value, null)
			: r.Fallback == "wallet" && fromWallet is int f ? (f, null)
			: (null, gems.Problem ?? "the card's gem value isn't known");
	}

	private static async Task<long?> PackWorthAsync(Rule r, uint app, TradeValues v, CancellationToken ct) {
		CardSetInfo? set = await v.SetOfAsync(app, ct).ConfigureAwait(false);

		if (set is not { SetSize: > 0 }) {
			return r.Value == "fixed" ? r.Gems : null;
		}

		long value;

		if (r.Value == "fixed") {
			value = r.Gems;
		} else if (r.Value == "setSize") {
			value = (int) Math.Round(6000.0 / set.SetSize, MidpointRounding.AwayFromZero);
		} else {
			// Expected and floor need the prices of the cards in the set. The plugin API gives the size and how many are
			// held, not the card hashes, so with a size-only set there is nothing to price: Unknown, never a free booster.
			if (set.Cards.Count == 0) {
				return null;
			}

			List<long> nets = [];
			TimeSpan age = TimeSpan.FromHours(Math.Clamp(r.MaxAgeHours, 1, 24 * 30));

			foreach (string hash in set.Cards) {
				MarketQuote? quote = await v.QuoteAsync(app, hash, ct, age).ConfigureAwait(false);

				if (quote == null || quote.Cents <= 0 || !quote.Currency.Equals(v.Currency, StringComparison.OrdinalIgnoreCase)
					|| (v.Now - quote.At > age) || (quote.At > v.Now.AddHours(1))) {
					return null;
				}

				int buyer = Math.Max(3, quote.Cents - Math.Max(1, (int) Math.Round(quote.Cents * Math.Clamp(r.UndercutPct, 0, 99) / 100.0, MidpointRounding.AwayFromZero)));
				int? net = TradeValues.SellerNet(buyer);

				if (net is not int n || n <= 0) {
					return null;
				}

				nets.Add(n);
			}

			if (nets.Count == 0) {
				return null;
			}

			if (r.Value == "floor") {
				value = r.FloorMode == "threeTimesCheapest" ? 3 * nets.Min() : nets.Order().Take(3).Sum();
			} else {
				value = 3 * (long) Math.Round(nets.Average(), MidpointRounding.ToZero);

				if (value == 0) {
					return null;
				}
			}
		}

		bool inGems = r.Value is "setSize" or "fixed";

		if (r.ValueIn == "gems") {
			return inGems ? value : await v.SackAsync(ct).ConfigureAwait(false) is int s and > 0 ? (long) value * 1000 / s : null;
		}

		return inGems ? await v.SackAsync(ct).ConfigureAwait(false) is int s2 and > 0 ? (long) value * s2 / 1000 : null : value;
	}

	private static (TradeVerdict, string) Compare(long? payment, long required, string unit, string what) =>
		payment is not long paid ? (TradeVerdict.Unknown, "the payment's value isn't known")
			: paid >= required ? (TradeVerdict.Accept, $"{what} at least {required} {unit}")
			: (TradeVerdict.Abstain, $"{what} {required} {unit}, but the offer pays {paid}");

	private static (TradeVerdict, string) CompareAtMost(long? payment, long allowed, string unit, string what) =>
		payment is not long paid ? (TradeVerdict.Unknown, "the payment's value isn't known")
			: paid <= allowed ? (TradeVerdict.Accept, $"{what} is {allowed} {unit}")
			: (TradeVerdict.Abstain, $"{what} is {allowed} {unit}, but the offer gives {paid}");
}
