using System.Globalization;
using GamerFarm.Addons.TradeRules.Core;
using GamerFarm.Plugins;
using static GamerFarm.Addons.TradeRules.Texts;

namespace GamerFarm.Addons.TradeRules;

/// <summary>
/// The add-on's one command. It explains the rules and edits the add-on's own settings; it never answers an offer - the
/// judge is the only place that does, and only after the app has left the offer undecided.
/// </summary>
public sealed partial class TradeRulesAddon {
	private const string Usage = "[preview|explain|on|off|preset|export|import|copy] [account] [json|preset id]";

	internal Task<string> CommandAsync(string[] args) => Task.FromResult(Command(args));

	private string Command(string[] args) {
		string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
		string? account = args.Length > 1 ? args[1] : null;
		string rest = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "";

		switch (sub) {
			case "status":
				return Status();

			case "preview":
			case "explain": {
				if (account == null) {
					return Verb + " preview <account> [json]";
				}

				if (Host.Account(account) == null) {
					return T("there's no account called '{0}'", account);
				}

				RuleSet set = rest.Length > 0 ? RuleCodec.Parse(rest) : Parsed(account);
				List<string> lines = [.. TradePolicy.Explain(set)];
				lines.Insert(0, T("Preview for {0} (explains only - nothing is answered, and no price is asked):", account));

				return string.Join('\n', lines);
			}

			case "on":
			case "off": {
				if (account == null) {
					return $"{Verb} {sub} <account>";
				}

				if (Host.Account(account) == null) {
					return T("there's no account called '{0}'", account);
				}

				return Host.SetSetting(SetEnabled, sub == "on" ? "true" : "false", account)
					? T("{0}: custom trade rules {1}", account, sub == "on" ? T("on") : T("off"))
					: T("the setting couldn't be changed for {0}", account);
			}

			case "preset": {
				if (account == null) {
					return T("Presets:") + "\n  " + string.Join("\n  ", RulePresets.All().Select(p => $"{p.Id}  -  {p.Label}"))
						+ "\n" + T("Apply one with 'traderules preset <account> <id>'.");
				}

				if (Host.Account(account) == null) {
					return T("there's no account called '{0}'", account);
				}

				if (rest.Length == 0) {
					return T("Presets:") + "\n  " + string.Join("\n  ", RulePresets.All().Select(p => $"{p.Id}  -  {p.Label}"));
				}

				if (RulePresets.Find(rest) is not { } preset) {
					return T("there's no preset called '{0}'", rest);
				}

				string json = RuleCodec.Write([preset.Rule]);

				return Host.SetSetting(SetRules, json, account)
					? T("{0}: preset {1} loaded. Preview it before switching the rules on.", account, preset.Id)
					: T("the setting couldn't be changed for {0}", account);
			}

			case "export": {
				if (account == null) {
					return Verb + " export <account>";
				}

				if (Host.Account(account) == null) {
					return T("there's no account called '{0}'", account);
				}

				RuleSet set = Parsed(account);
				return set.Any ? RuleCodec.Write(set.Rules) : T("{0} has no rules saved", account);
			}

			case "import": {
				if ((account == null) || (rest.Length == 0)) {
					return Verb + " import <account> <rules json>";
				}

				if (Host.Account(account) == null) {
					return T("there's no account called '{0}'", account);
				}

				RuleSet set = RuleCodec.Parse(rest);

				if (set.Problems.Count > 0) {
					return T("the rules weren't saved:") + "\n  " + string.Join("\n  ", set.Problems);
				}

				string json = set.Any ? RuleCodec.Write(set.Rules) : "";

				return Host.SetSetting(SetRules, json, account)
					? T("{0}: saved {1} rule(s)", account, set.Rules.Count)
					: T("the setting couldn't be changed for {0}", account);
			}

			case "copy": {
				string? to = args.Length > 2 ? args[2] : null;

				if ((account == null) || (to == null)) {
					return Verb + " copy <from> <to>";
				}

				if ((Host.Account(account) == null) || (Host.Account(to) == null)) {
					return T("both accounts must exist");
				}

				return Host.SetSetting(SetRules, RulesOf(account), to)
					? T("rules copied from {0} to {1}; the destination's on/off switch was left alone", account, to)
					: T("the setting couldn't be changed for {0}", to);
			}

			default:
				return Verb + " " + Usage;
		}
	}

	private string Status() {
		List<string> lines = [T("Custom trade rules (the judge is only asked about offers the app leaves undecided):")];

		foreach (IPluginAccount a in Host.Accounts) {
			RuleSet set = Parsed(a.Name);
			lines.Add($"  {a.Name,-16} {(Enabled(a.Name) ? T("on") : T("off")),-4} {set.Rules.Count} rule(s)"
				+ (set.Problems.Count > 0 ? $"  [{string.Join("; ", set.Problems)}]" : ""));
		}

		if (Host.Accounts.Count == 0) {
			lines.Add("  " + T("no accounts configured"));
		}

		lines.Add(T("Preview a set with 'traderules preview <account>'. Edit the JSON under Plugins - trade rules - Rules JSON, or with 'traderules preset/import'."));

		return string.Join('\n', lines);
	}

	private static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
}
