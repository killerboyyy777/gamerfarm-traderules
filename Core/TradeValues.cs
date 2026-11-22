namespace GamerFarm.Addons.TradeRules.Core;

/// <summary>How far one look may go for a price.</summary>
public enum ValueLookups {
	/// <summary>Only what the app already holds. A missing one stays Unknown; nothing is asked.</summary>
	Cached,

	/// <summary>A held price is used however old; one that isn't held is asked for (the default).</summary>
	Missing,

	/// <summary>Like <see cref="Missing"/>, and a held price too old for this rule is asked for again.</summary>
	Stale
}

/// <summary>
/// Where a rule's values come from. The real one reads the app's prices, gem values and card sets through the host; the
/// checks use a fake with no network.
/// </summary>
public interface IValueSource {
	/// <summary>The wallet currency's code. Empty when Steam hasn't said; a market quote in another code is never used.</summary>
	string Currency { get; }

	/// <summary>What "now" means for a price's age.</summary>
	DateTime Now { get; }

	/// <summary>The item kind as the app described it.</summary>
	SpareKind KindOf(RuleItem item);

	/// <summary>A gem value held already, if the source has one. Never an ask.</summary>
	int? CachedGems(RuleItem item);

	/// <summary>One item's gem value, asking the app when it isn't held (budgeted).</summary>
	Task<int?> GemsAsync(RuleItem item, CancellationToken ct);

	/// <summary>A market price held already, never an ask. Its currency code is part of the answer.</summary>
	MarketQuote? CachedQuote(uint appId, string hash);

	/// <summary>A market price now, asking through the app when needed (budgeted).</summary>
	Task<MarketQuote?> QuoteAsync(uint appId, string hash, CancellationToken ct);

	/// <summary>A game's card set, or null when the app doesn't know its size.</summary>
	CardSetInfo? CardSet(uint appId);

	/// <summary>What a sack of 1,000 gems pays after Steam's fee, from a price already held.</summary>
	int? SackNetCents();

	/// <summary>What a sack pays, asking for its price when it isn't held (budgeted).</summary>
	Task<int?> SackNetCentsAsync(CancellationToken ct);
}

/// <summary>What one item is worth in each basis a rule may ask for. Null means Unknown; 0 is a real zero.</summary>
public sealed record ValuedItem(RuleItem Item, SpareKind Kind, int? Gems, int? WalletCents, int? SetSize, bool SetKnown, string? Problem) {
	/// <summary>How many copies this value is for; totals multiply by it.</summary>
	public int Count => Item.Count;
}

/// <summary>
/// The two sides of one offer, valued lazily. A rule about counts reads nothing; a gem rule asks only for gems, a wallet
/// rule only for prices - at most eight uncached asks and ten seconds for the whole offer.
/// </summary>
public sealed class TradeValues {
	/// <summary>The most uncached asks one offer may make.</summary>
	public const int MaxLookups = 8;

	/// <summary>How long one offer's valuation may take.</summary>
	public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

	/// <summary>The market hash name of a sack of 1,000 gems.</summary>
	public const string SackHash = "753-Sack of Gems";

	private const int SackApp = 753, GemsInASack = 1000, MarketFloor = 3;

	private readonly Dictionary<(uint App, string Hash), MarketQuote?> _quotes = [];
	private readonly Dictionary<uint, CardSetInfo?> _sets = [];
	private readonly Dictionary<ulong, int?> _gems = [];
	private readonly ValueLookups _lookups;
	private readonly DateTime _until;
	private int _asks;
	private int? _sack;

	private TradeValues(IValueSource source, OfferPartner partner, IReadOnlyList<RuleItem> giving, IReadOnlyList<RuleItem> receiving, ValueLookups lookups) {
		Source = source;
		Partner = partner;
		Giving = giving;
		Receiving = receiving;
		_lookups = lookups;
		_until = DateTime.UtcNow + Budget;
	}

	/// <summary>Where the values come from.</summary>
	public IValueSource Source { get; }

	/// <summary>Who sent the offer, for the per-rule partner filters.</summary>
	public OfferPartner Partner { get; }

	/// <summary>What the account would give away.</summary>
	public IReadOnlyList<RuleItem> Giving { get; }

	/// <summary>What the account would receive.</summary>
	public IReadOnlyList<RuleItem> Receiving { get; }

	/// <summary>The account's wallet currency code, as the app knows it.</summary>
	public string Currency => Source.Currency;

	/// <summary>What "now" means for a price's age.</summary>
	public DateTime Now => Source.Now;

	/// <summary>A rule needed a held price that was too old; asking the market again could help.</summary>
	public bool PriceStale { get; private set; }

	/// <summary>A rule needed a price that wasn't held, or the market didn't answer.</summary>
	public bool PriceMissing { get; private set; }

	/// <summary>The eight asks or the ten seconds ran out before the offer could be valued.</summary>
	public bool OutOfLookups { get; private set; }

	/// <summary>Whether a second pass could turn an Unknown into an answer.</summary>
	public bool RefreshWanted => PriceStale || PriceMissing;

	/// <summary>Value one offer from a source. The one the judge uses.</summary>
	public static TradeValues From(IValueSource source, IReadOnlyList<RuleItem> giving, IReadOnlyList<RuleItem> receiving,
		OfferPartner? partner = null, ValueLookups lookups = ValueLookups.Missing) =>
		new(source, partner ?? new OfferPartner(0, ""), giving, receiving, lookups);

	private bool Spend() {
		if (DateTime.UtcNow > _until) {
			OutOfLookups = true;
			return false;
		}

		if (++_asks > MaxLookups) {
			OutOfLookups = true;
			return false;
		}

		return true;
	}

	private bool TooOld(MarketQuote quote, TimeSpan age) {
		if (quote.At > Now.AddHours(1)) {
			return true;
		}

		return Now - quote.At > age;
	}

	/// <summary>The item's gem value, in gems per copy (loose gems 1, sacks 1000). Never wallet cents.</summary>
	public async Task<ValuedItem> GemsAsync(RuleItem item, CancellationToken ct) {
		SpareKind kind = Source.KindOf(item);

		if (kind == SpareKind.Gems) {
			return new ValuedItem(item, kind, 1, null, null, false, null);
		}

		if (kind == SpareKind.GemSack) {
			return new ValuedItem(item, kind, GemsInASack, null, null, false, null);
		}

		if (Source.CachedGems(item) is int held) {
			return new ValuedItem(item, kind, held, null, null, false, null);
		}

		if (_gems.TryGetValue(item.AssetId, out int? learned)) {
			return learned is int g ? new ValuedItem(item, kind, g, null, null, false, null)
				: new ValuedItem(item, kind, null, null, null, false, "Steam hasn't said what it's worth in gems");
		}

		if (_lookups == ValueLookups.Cached) {
			PriceMissing = true;

			return new ValuedItem(item, kind, null, null, null, false, "its gem value isn't held, and this look doesn't ask");
		}

		if (!Spend()) {
			return new ValuedItem(item, kind, null, null, null, false, "the offer is out of value lookups for this look");
		}

		int? value = await Source.GemsAsync(item, ct).ConfigureAwait(false);
		_gems[item.AssetId] = value;

		return value is int v ? new ValuedItem(item, kind, v, null, null, false, null)
			: new ValuedItem(item, kind, null, null, null, false, "Steam didn't say what it's worth in gems");
	}

	/// <summary>The item's wallet value: what selling one copy pays after Steam's fee, in the wallet's currency.</summary>
	public async Task<ValuedItem> WalletAsync(RuleItem item, bool giving, int minSales, int maxAgeHours, double undercutPct, string looseGems,
		CancellationToken ct) {
		SpareKind kind = Source.KindOf(item);

		if (kind == SpareKind.Gems) {
			int? loose = looseGems == "sack" ? await SackAsync(ct).ConfigureAwait(false) is int sack ? sack / GemsInASack : null : null;

			return loose is int l ? new ValuedItem(item, kind, 1, l, null, false, null)
				: new ValuedItem(item, kind, 1, null, null, false, "loose gems aren't listed on the market, and there's no sack price to work out what they pay");
		}

		if (kind == SpareKind.GemSack) {
			return await SackAsync(ct).ConfigureAwait(false) is int sack
				? new ValuedItem(item, kind, GemsInASack, sack, null, false, null)
				: new ValuedItem(item, kind, GemsInASack, null, null, false, "there's no price for a sack of gems right now");
		}

		if (!item.Marketable || !item.Described) {
			return new ValuedItem(item, kind, null, null, null, false, "it isn't sold on the Steam Community Market");
		}

		if (Currency.Length == 0) {
			return new ValuedItem(item, kind, null, null, null, false, "Steam hasn't said this account's wallet currency");
		}

		if (item.Hash.Length == 0) {
			return new ValuedItem(item, kind, null, null, null, false, "Steam didn't say its market name");
		}

		TimeSpan age = TimeSpan.FromHours(Math.Clamp(maxAgeHours, 1, 24 * 30));
		MarketQuote? quote = Source.CachedQuote(item.AppId, item.Hash) ?? _quotes.GetValueOrDefault((item.AppId, item.Hash));

		if (quote == null) {
			if (_lookups == ValueLookups.Cached) {
				PriceMissing = true;

				return new ValuedItem(item, kind, null, null, null, false, "no price is held for it, and this look doesn't ask the market");
			}

			if (!Spend()) {
				return new ValuedItem(item, kind, null, null, null, false, "the offer is out of value lookups for this look");
			}

			quote = await Source.QuoteAsync(item.AppId, item.Hash, ct).ConfigureAwait(false);
			_quotes[(item.AppId, item.Hash)] = quote;
		} else if ((_lookups == ValueLookups.Stale) && TooOld(quote, age)) {
			if (!Spend()) {
				PriceStale = true;

				return new ValuedItem(item, kind, null, null, null, false, "the offer is out of value lookups for this look");
			}

			quote = await Source.QuoteAsync(item.AppId, item.Hash, ct).ConfigureAwait(false) ?? quote;
			_quotes[(item.AppId, item.Hash)] = quote;
		}

		if (quote == null) {
			PriceMissing = true;

			return new ValuedItem(item, kind, null, null, null, false, "the market didn't answer for it");
		}

		if (TooOld(quote, age)) {
			PriceStale = true;

			return new ValuedItem(item, kind, null, null, null, false, "its price is older than the rule allows");
		}

		if (!quote.Currency.Equals(Currency, StringComparison.OrdinalIgnoreCase)) {
			return new ValuedItem(item, kind, null, null, null, false, $"the market price is in {quote.Currency}, not this account's {Currency}");
		}

		if (quote.Cents <= 0) {
			PriceMissing = true;

			return new ValuedItem(item, kind, null, null, null, false, "there's no listing to go by");
		}

		int net = SellerNet(BuyerCents(quote, giving, undercutPct));

		return net > 0 ? new ValuedItem(item, kind, null, net, null, false, null)
			: new ValuedItem(item, kind, null, null, null, false, "its price can't be trusted (a dumped listing, or no recent sale)");
	}

	/// <summary>One market price, held or freshly asked, for the booster rules' card lists.</summary>
	public async Task<MarketQuote?> QuoteAsync(uint appId, string hash, CancellationToken ct, TimeSpan? maxAge = null) {
		if (hash.Length == 0) {
			return null;
		}

		MarketQuote? quote = Source.CachedQuote(appId, hash) ?? _quotes.GetValueOrDefault((appId, hash));

		if (quote == null) {
			if (_lookups == ValueLookups.Cached) {
				PriceMissing = true;
				return null;
			}

			if (!Spend()) {
				return null;
			}

			quote = await Source.QuoteAsync(appId, hash, ct).ConfigureAwait(false);
			_quotes[(appId, hash)] = quote;

			if (quote == null) {
				PriceMissing = true;
			}

			return quote;
		}

		if ((_lookups == ValueLookups.Stale) && (maxAge is TimeSpan age) && TooOld(quote, age)) {
			if (!Spend()) {
				PriceStale = true;

				return quote;
			}

			MarketQuote? fresh = await Source.QuoteAsync(appId, hash, ct).ConfigureAwait(false);

			if (fresh != null) {
				quote = fresh;
				_quotes[(appId, hash)] = quote;
			}
		}

		if ((maxAge is TimeSpan limit) && TooOld(quote, limit)) {
			PriceStale = true;
		}

		return quote;
	}

	/// <summary>The item's game's card set and its size.</summary>
	public Task<ValuedItem> SetAsync(RuleItem item, CancellationToken ct) {
		CardSetInfo? set = SetOf(item.Game);

		return Task.FromResult(set is { SetSize: > 0 }
			? new ValuedItem(item, item.Kind, null, null, set.SetSize, true, null)
			: new ValuedItem(item, item.Kind, null, null, null, false, "its set isn't known whole"));
	}

	/// <summary>The game's card set, for a booster rule that needs the cards behind it.</summary>
	public Task<CardSetInfo?> SetOfAsync(uint appId, CancellationToken ct) => Task.FromResult(SetOf(appId));

	private CardSetInfo? SetOf(uint appId) {
		if (_sets.TryGetValue(appId, out CardSetInfo? cached)) {
			return cached;
		}

		CardSetInfo? set = Source.CardSet(appId);
		_sets[appId] = set;

		if (set == null) {
			PriceMissing = true;
		}

		return set;
	}

	/// <summary>What a sack of 1,000 gems pays after Steam's fee, in the wallet's currency.</summary>
	public async Task<int?> SackAsync(CancellationToken ct) {
		if (_sack is int known) {
			return known;
		}

		if (Source.SackNetCents() is int held) {
			_sack = held;
			return held;
		}

		if (_lookups == ValueLookups.Cached) {
			PriceMissing = true;
			return null;
		}

		if (!Spend()) {
			return null;
		}

		int? asked = await Source.SackNetCentsAsync(ct).ConfigureAwait(false);
		_sack = asked;

		if (asked == null) {
			PriceMissing = true;
		}

		return asked;
	}

	/// <summary>What a side is worth in gems: all of it, or null when any item's value is unknown.</summary>
	public async Task<long?> GemsOfAsync(IReadOnlyList<RuleItem> side, CancellationToken ct) {
		long total = 0;

		foreach (RuleItem item in side) {
			ValuedItem v = await GemsAsync(item, ct).ConfigureAwait(false);

			if (v.Gems is not int gems) {
				return null;
			}

			total += (long) gems * v.Count;
		}

		return total;
	}

	/// <summary>What a side pays on the market: all of it, or null when any item's value is unknown.</summary>
	public async Task<long?> WalletOfAsync(IReadOnlyList<RuleItem> side, bool giving, int minSales, int maxAgeHours, double undercutPct,
		string looseGems, CancellationToken ct) {
		long total = 0;

		foreach (RuleItem item in side) {
			ValuedItem v = await WalletAsync(item, giving, minSales, maxAgeHours, undercutPct, looseGems, ct).ConfigureAwait(false);

			if (v.WalletCents is not int cents) {
				return null;
			}

			total += (long) cents * v.Count;
		}

		return total;
	}

	/// <summary>Steam's cut: 5 percent for Steam and 10 percent for the game, each at least a cent.</summary>
	public static int SellerNet(int buyer) {
		for (int receive = Math.Min(buyer - 2, 1_000_000_000); receive >= 1; receive--) {
			int fees = Math.Max(1, receive * 5 / 100) + Math.Max(1, receive * 10 / 100);

			if ((long) receive + fees <= buyer) {
				return receive;
			}
		}

		return 0;
	}

	private static int BuyerCents(MarketQuote quote, bool giving, double undercutPct) {
		// The plugin API carries the lowest listing, not the median or a sales count. Under the "conservative" rule the
		// giving side counts the higher and the receiving side the lower of what is known; with one price, both are it.
		int price = quote.Cents;

		if (undercutPct > 0) {
			price -= Math.Max(1, (int) Math.Round(price * Math.Clamp(undercutPct, 0, 99) / 100.0, MidpointRounding.AwayFromZero));
		}

		return Math.Max(MarketFloor, price);
	}
}
