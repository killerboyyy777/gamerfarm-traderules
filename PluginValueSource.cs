using GamerFarm.Addons.TradeRules.Core;
using GamerFarm.Plugins;

namespace GamerFarm.Addons.TradeRules;

/// <summary>
/// The real values: the app's own market prices and gem values, and the card sets it has already read. Nothing here guesses
/// a number, and nothing here goes to the network directly - even an ask goes through the host's own queue.
/// </summary>
internal sealed class PluginValueSource : IValueSource {
	private readonly IPluginHost _host;
	private readonly IPluginAccount _account;
	private readonly Dictionary<ulong, PluginTradeItem> _items = new();

	public PluginValueSource(IPluginHost host, IPluginAccount account, PluginTradeOffer offer) {
		_host = host;
		_account = account;

		foreach (PluginTradeItem item in offer.Giving.Concat(offer.Receiving)) {
			_items[item.AssetId] = item;
		}
	}

	/// <summary>The wallet currency's code; empty until Steam has told the app.</summary>
	public string Currency => _account.WalletCurrency ?? "";

	/// <summary>What "now" means for a price's age.</summary>
	public DateTime Now => DateTime.UtcNow;

	/// <summary>The kind the app described in the offer.</summary>
	public SpareKind KindOf(RuleItem item) => item.Kind;

	/// <summary>No synchronous gem source exists in the plugin API; an ask is always budgeted and async.</summary>
	public int? CachedGems(RuleItem item) => null;

	/// <summary>Ask the app for one item's gem value.</summary>
	public Task<int?> GemsAsync(RuleItem item, CancellationToken ct) =>
		_items.TryGetValue(item.AssetId, out PluginTradeItem? original) ? _host.GemValueAsync(original, ct) : Task.FromResult<int?>(null);

	/// <summary>A market price the app holds, never an ask.</summary>
	public MarketQuote? CachedQuote(uint appId, string hash) => MarketQuote.From(_host.KnownMarketPrice(appId, hash));

	/// <summary>A market price, asked through the app's own queue when it isn't held fresh.</summary>
	public async Task<MarketQuote?> QuoteAsync(uint appId, string hash, CancellationToken ct) =>
		MarketQuote.From(await _host.MarketPriceAsync(appId, hash, ct).ConfigureAwait(false));

	/// <summary>A card set the app knows whole; the API gives its size and owned count, not the card hashes.</summary>
	public CardSetInfo? CardSet(uint appId) {
		PluginCardSet? set = _account.CardSet(appId);

		return set is { SetSize: > 0 } s ? new CardSetInfo(s.AppId, s.Game, s.SetSize, s.Owned, []) : null;
	}

	/// <summary>What a sack pays after Steam's fee, from a price already held.</summary>
	public int? SackNetCents() {
		MarketQuote? quote = CachedQuote(753, TradeValues.SackHash);

		return quote is { Cents: > 0 } && quote.Currency.Equals(Currency, StringComparison.OrdinalIgnoreCase)
			? TradeValues.SellerNet(quote.Cents)
			: null;
	}

	/// <summary>What a sack pays, asking the app for its price when nothing fresh is held.</summary>
	public async Task<int?> SackNetCentsAsync(CancellationToken ct) {
		MarketQuote? quote = await QuoteAsync(753, TradeValues.SackHash, ct).ConfigureAwait(false);

		return quote is { Cents: > 0 } && quote.Currency.Equals(Currency, StringComparison.OrdinalIgnoreCase)
			? TradeValues.SellerNet(quote.Cents)
			: null;
	}
}
