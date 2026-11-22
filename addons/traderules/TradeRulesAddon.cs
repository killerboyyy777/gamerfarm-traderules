using System.Globalization;
using GamerFarm.Addons.TradeRules.Core;
using GamerFarm.Plugins;
using static GamerFarm.Addons.TradeRules.Texts;

namespace GamerFarm.Addons.TradeRules;

/// <summary>
/// Custom trade rules for gamer.farm: a per-account set of typed rules and small formulas that answer the offers the app
/// leaves undecided. Off until the operator switches an account on, and never a way past the app's own safety checks.
/// </summary>
public sealed partial class TradeRulesAddon : IGamerPlugin {
	/// <summary>Which gamer.farm this needs at least: gem values, card sets and the wallet came in 2.0.3.</summary>
	internal const string MinAppVersion = "2.0.3";

	internal const string SetEnabled = "Enabled", SetRules = "RulesJson", SetAllow = "PartnersAllow", SetDeny = "PartnersDeny";

	/// <summary>The command every page button calls. It explains and edits settings; it never answers an offer itself.</summary>
	internal const string Verb = "traderules";

	public string Name => "traderules";

	public string Version { get; } = typeof(TradeRulesAddon).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

	internal IPluginHost Host { get; private set; } = null!;

	internal RuleState State { get; private set; } = new();

	private readonly Dictionary<string, string> _said = new(StringComparer.Ordinal);
	private readonly Lock _stateGate = new();

	/// <summary>Declare settings, the judge, the command and the page. A version too old disables the add-on.</summary>
	public async Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
		Host = host;
		Texts.Language = () => host.Language;

		if (!System.Version.TryParse(host.AppVersion, out System.Version? app) || (app < new System.Version(2, 0, 3))) {
			host.Log(T("trade rules need gamer.farm {0} or newer - this is {1}; not starting", MinAppVersion, host.AppVersion));
			return;
		}

		Declare(host);
		State = RuleState.Read(await host.LoadStateAsync().ConfigureAwait(false));
		host.AddTradeJudge(JudgeAsync);
		host.AddCommand(Verb,
			T("[preview|explain|on|off|preset|export|import|copy] [account] [json|preset id]"),
			T("Custom trade rules. Bare: a summary. 'preview <account> [json]' explains a rule set without acting on an offer. on/off, preset, export, import and copy only edit the add-on's settings; no offer is answered from here."),
			CommandAsync);
		host.AddPage(T("Trade rules"), RenderAsync);
	}

	/// <summary>Nothing to stop: the add-on keeps no background work of its own.</summary>
	public Task OnUnloadAsync() => Task.CompletedTask;

	private static void Declare(IPluginHost host) {
		host.AddSetting(new PluginSetting(SetEnabled, T("Use custom trade rules"),
			T("Off by default. When on, this account's rules answer only the offers the app has left undecided, after its own safety checks. Preview the rules before switching on."),
			PluginSettingKind.Bool, "false", PerAccount: true));
		host.AddSetting(new PluginSetting(SetRules, T("Rules JSON"),
			T("This account's rules, version 1. Empty means no rules. The Trade rules page shows presets and a preview; a bad rule is skipped, never guessed."),
			PluginSettingKind.Text, "", PerAccount: true));
		host.AddSetting(new PluginSetting(SetAllow, T("Only answer offers from"),
			T("Comma-separated SteamID64s or local account names. Empty means anybody. Names are matched without regard to case; a deny always wins."),
			PluginSettingKind.Text, "", PerAccount: true));
		host.AddSetting(new PluginSetting(SetDeny, T("Never answer offers from"),
			T("Comma-separated SteamID64s or local account names. A name here stops the rules before they run, whatever they say."),
			PluginSettingKind.Text, "", PerAccount: true));
	}

	internal static bool IsOn(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);

	internal static IReadOnlyList<string> Lines(string value) =>
		[.. value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

	internal string RulesOf(string account) => Host.Setting(SetRules, account) ?? "";

	internal RuleSet Parsed(string account) => RuleCodec.Parse(RulesOf(account));

	internal bool Enabled(string account) => IsOn(Host.Setting(SetEnabled, account) ?? "");

	/// <summary>Say a problem once, so a repeated look doesn't fill the log.</summary>
	internal void Said(string key, string line) {
		lock (_said) {
			if (_said.TryGetValue(key, out string? was) && (was == line)) {
				return;
			}

			_said[key] = line;
		}

		Host.Log("traderules: " + line);
	}

	// -- the judge -------------------------------------------------------------
	internal async Task<PluginTradeAnswer> JudgeAsync(PluginTradeOffer offer, CancellationToken ct) {
		try {
			IPluginAccount? account = Host.Account(offer.Account);

			if (account == null) {
				return Leave("the account is gone");
			}

			if (!Enabled(account.Name)) {
				return Leave("custom rules are off for this account");
			}

			IReadOnlyList<string> allow = Lines(Host.Setting(SetAllow, account.Name) ?? "");
			IReadOnlyList<string> deny = Lines(Host.Setting(SetDeny, account.Name) ?? "");
			List<RuleItem> giving = [.. offer.Giving.Select(RuleItem.From)];
			List<RuleItem> receiving = [.. offer.Receiving.Select(RuleItem.From)];
			OfferPartner partner = new(offer.PartnerSteamId, PartnerName(offer));
			GateResult gates = RuleGates.Check(new OfferShape(partner, giving, receiving, offer.TradeHoldDays), allow, deny);

			if (gates.Block != GateBlock.None) {
				Remember(account.Name, offer.OfferId, gates.Block.ToString(), "", gates.Why);
				return Leave(gates.Why);
			}

			RuleSet set = Parsed(account.Name);

			if (set.Problems.Count > 0) {
				Said(account.Name + ":problems", string.Join("; ", set.Problems));
			}

			if (!set.Any) {
				return Leave("no custom rule applies");
			}

			PluginValueSource source = new(Host, account, offer);
			TradeValues values = TradeValues.From(source, giving, receiving, partner);
			TradeDecision decision = await TradePolicy.DecideAsync(set, values, gates, ct).ConfigureAwait(false);

			// A rule stopped at a missing or too-old value: ask once more, then decide again. The app's own queues and this
			// add-on's eight-ask budget still apply; a second Unknown leaves the offer exactly as before.
			if ((decision.Verdict == TradeVerdict.Unknown) && values.RefreshWanted && MayRefresh(account.Name, offer.OfferId)) {
				TradeValues fresh = TradeValues.From(source, giving, receiving, partner, ValueLookups.Stale);
				decision = await TradePolicy.DecideAsync(set, fresh, gates, ct).ConfigureAwait(false);
			}

			Remember(account.Name, offer.OfferId, decision.Verdict.ToString(), decision.RuleId ?? "", decision.Why);
			string reason = $"{decision.Verdict}" + (decision.RuleId is { Length: > 0 } id ? $" by {id}" : "") + $": {decision.Why}";

			return decision.Verdict switch {
				TradeVerdict.Accept => PluginTradeAnswer.Accept(reason),
				TradeVerdict.Decline => PluginTradeAnswer.Decline(reason),
				_ => Leave(reason)
			};
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Host.Log($"traderules: could not judge offer {offer.OfferId}: {e.GetType().Name}: {e.Message}");
			return Leave("the add-on could not weigh this offer");
		}
	}

	private string PartnerName(PluginTradeOffer offer) =>
		Host.Accounts.FirstOrDefault(a => (a.SteamId == offer.PartnerSteamId) && !a.Name.Equals(offer.Account, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

	private static PluginTradeAnswer Leave(string why) => PluginTradeAnswer.Leave(why);

	/// <summary>Once every ten minutes for one offer, and at most three offers a minute.</summary>
	private bool MayRefresh(string account, ulong offer) {
		string key = account + ":" + offer.ToString(CultureInfo.InvariantCulture);
		DateTime now = DateTime.UtcNow;

		lock (_stateGate) {
			if (State.RefreshedAt.TryGetValue(key, out long at) && (now - new DateTime(at, DateTimeKind.Utc) < TimeSpan.FromMinutes(10))) {
				return false;
			}

			DateTime window = new(State.RefreshedAt.TryGetValue("window", out long w) ? w : DateTime.MinValue.Ticks, DateTimeKind.Utc);

			if ((now - window) >= TimeSpan.FromMinutes(1)) {
				State.RefreshedAt["window"] = now.Ticks;
				State.RefreshedAt["window-count"] = 1;
			} else {
				int used = (int) State.RefreshedAt.GetValueOrDefault("window-count");

				if (used >= 3) {
					return false;
				}

				State.RefreshedAt["window-count"] = used + 1;
			}

			State.RefreshedAt[key] = now.Ticks;

			foreach (string old in State.RefreshedAt.Keys.Where(k => k.Contains(':', StringComparison.Ordinal)
				&& !k.StartsWith("window", StringComparison.Ordinal)
				&& (now - new DateTime(State.RefreshedAt[k], DateTimeKind.Utc) > TimeSpan.FromHours(1))).ToList()) {
				State.RefreshedAt.Remove(old);
			}
		}

		_ = SaveAsync();
		return true;
	}

	internal void Remember(string account, ulong offer, string verdict, string rule, string why) {
		lock (_stateGate) {
			State.Note(new LastAnswer(account, offer, verdict, rule, why));
		}

		_ = SaveAsync();
	}

	internal async Task SaveAsync() {
		try {
			string json;

			lock (_stateGate) {
				json = State.Write();
			}

			await Host.SaveStateAsync(json).ConfigureAwait(false);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
			Host.Log("traderules: could not save the add-on's state: " + e.Message);
		}
	}

	internal IReadOnlyList<LastAnswer> Recent() {
		lock (_stateGate) {
			return [.. State.Answers];
		}
	}
}
