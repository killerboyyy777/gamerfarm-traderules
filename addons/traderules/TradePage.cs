using GamerFarm.Addons.TradeRules.Core;
using GamerFarm.Plugins;
using static GamerFarm.Addons.TradeRules.Texts;

namespace GamerFarm.Addons.TradeRules;

/// <summary>
/// The dashboard tab. The page API is text-only, so the JSON editor lives in the add-on's per-account "Rules JSON" setting
/// under Plugins; this tab shows the current text, the presets, and buttons that call the add-on's own command.
/// </summary>
public sealed partial class TradeRulesAddon {
	internal Task<PluginPage> RenderAsync(string? view) {
		List<PluginBlock> blocks = [];

		if (view is { Length: > 0 } v && v.StartsWith("account:", StringComparison.Ordinal)) {
			AccountPage(blocks, v[8..]);
		} else if (view is { Length: > 0 } p && p.StartsWith("presets:", StringComparison.Ordinal)) {
			PresetPage(blocks, p[8..]);
		} else {
			MainPage(blocks);
		}

		return Task.FromResult(new PluginPage(blocks));
	}

	private void MainPage(List<PluginBlock> blocks) {
		blocks.Add(PluginBlock.Heading(T("Custom trade rules")));
		blocks.Add(PluginBlock.Note(T("Off by default, per account. The judge is asked only about the offers the app leaves undecided, and the app still checks holds, tradability, promises and keep lists before it acts. A missing or stale value always leaves the offer; it is never read as zero.")));
		blocks.Add(PluginBlock.Line(T("Edit the rules under Plugins - trade rules - Rules JSON for an account, or load a preset here. Preview explains what is saved without acting on any offer.")));

		List<PluginRow> rows = [];

		foreach (IPluginAccount a in Host.Accounts) {
			RuleSet set = Parsed(a.Name);
			rows.Add(new PluginRow(
				[a.Name, Enabled(a.Name) ? T("on") : T("off"), set.Rules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
					set.Problems.Count == 0 ? T("none") : string.Join("; ", set.Problems)],
				new PluginButton(T("Rules"), View: "account:" + a.Name)));
		}

		blocks.Add(PluginBlock.Table([T("Account"), T("Rules on"), T("Rules"), T("Problems"), ""], rows));
		blocks.Add(PluginBlock.Note(T("The per-account switches are under Plugins - trade rules - Use custom trade rules. They stay off until you turn one on.")));

		foreach (IPluginAccount a in Host.Accounts) {
			blocks.Add(PluginBlock.ButtonRow(
				new PluginButton(T("Preview {0}", a.Name), $"{Verb} preview {a.Name}"),
				new PluginButton(T("Presets for {0}", a.Name), View: "presets:" + a.Name)));
		}

		IReadOnlyList<LastAnswer> recent = Recent();

		if (recent.Count > 0) {
			blocks.Add(PluginBlock.Heading(T("Recent answers")));
			blocks.Add(PluginBlock.Note(T("What the add-on last said. An answer only takes effect if every judge agrees and the app's own safety checks allow it.")));
			blocks.Add(PluginBlock.Table([T("Account"), T("Offer"), T("Answer"), T("Rule"), T("Why")],
				[.. recent.Select(r => new PluginRow([r.Account, r.OfferId.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Verdict, r.RuleId, r.Why]))]));
		}
	}

	private void AccountPage(List<PluginBlock> blocks, string account) {
		blocks.Add(PluginBlock.Heading(T("Rules for {0}", account)));
		blocks.Add(PluginBlock.ButtonRow(
			new PluginButton(T("Back"), View: ""),
			new PluginButton(T("Preview"), $"{Verb} preview {account}"),
			new PluginButton(T("Presets"), View: "presets:" + account),
			new PluginButton(T("Export"), $"{Verb} export {account}"),
			new PluginButton(Enabled(account) ? T("Turn off") : T("Turn on"), $"{Verb} {(Enabled(account) ? "off" : "on")} {account}")));

		RuleSet set = Parsed(account);
		string json = RulesOf(account);
		blocks.Add(PluginBlock.Line(json.Length == 0 ? T("No rules saved for this account yet.") : (json.Length > 12_000 ? json[..12_000] + "\n..." : json)));

		if (set.Problems.Count > 0) {
			blocks.Add(PluginBlock.Heading(T("Problems")));
			blocks.Add(PluginBlock.Line(string.Join("\n", set.Problems)));
		}

		blocks.Add(PluginBlock.Heading(T("What the rules say")));
		blocks.Add(PluginBlock.Table(["#", T("Priority"), T("Action"), T("Id"), T("Kind"), T("Meaning")],
			[.. set.Rules.Select((r, i) => new PluginRow([
				(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
				r.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
				r.Declines ? T("decline") : r.Action,
				r.Id, r.Kind, r.Note.Length > 0 ? r.Note : RuleCodec.Write([r])]))]));
	}

	private void PresetPage(List<PluginBlock> blocks, string account) {
		blocks.Add(PluginBlock.Heading(T("Presets for {0}", account)));
		blocks.Add(PluginBlock.Note(T("A preset replaces this account's saved rules. It stays off until you switch this account on, and you should preview it first. Every price and table is meant to be edited to your own rate card.")));
		blocks.Add(PluginBlock.ButtonRow(new PluginButton(T("Back"), View: "account:" + account)));
		blocks.Add(PluginBlock.Table([T("Preset"), T("What it does"), ""],
			[.. RulePresets.All().Select(p => new PluginRow([p.Label, p.Rule.Note],
				new PluginButton(T("Use"), $"{Verb} preset {account} {p.Id}", "account:" + account)))]));
	}
}
