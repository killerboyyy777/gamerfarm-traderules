using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GamerFarm.Addons.TradeRules.Core;

/// <summary>
/// Reads and writes the rule JSON. Strict when a page saves (every problem is returned, nothing applied), lenient when an
/// offer is judged (the bad rules are skipped, the rest still work) - never an exception, never a half-applied rule.
/// </summary>
public static class RuleCodec {
	/// <summary>The only format version accepted, and the limits on a pasted set.</summary>
	public const int Version = 1, MaxRules = 100, MaxTable = 100, MaxNames = 256;

	/// <summary>The most characters of rule JSON read.</summary>
	public const int MaxBytes = 64 * 1024;

	/// <summary>Rules and problems in a document. Null or blank is an empty set, never a problem.</summary>
	public static RuleSet Parse(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return RuleSet.Empty;
		}

		if (json.Length > MaxBytes) {
			return new RuleSet([], [$"the rules are too long ({json.Length} characters, at most {MaxBytes})"]);
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return new RuleSet([], ["the rules must be a JSON object"]);
			}

			return ParseObject(doc.RootElement);
		} catch (JsonException e) {
			return new RuleSet([], [$"the rules aren't valid JSON: {e.Message}"]);
		}
	}

	/// <summary>The saved form: version 1 and the rules, with only the fields their kind uses.</summary>
	public static string Write(IReadOnlyList<Rule> rules) {
		using MemoryStream stream = new();
		using (Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true })) {
			w.WriteStartObject();
			w.WriteNumber("version", Version);
			w.WriteStartArray("rules");

			foreach (Rule r in rules) {
				WriteRule(w, r);
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static RuleSet ParseObject(JsonElement root) {
		List<string> problems = [];
		int version = Int(root, "version") ?? 0;

		if (version != Version) {
			problems.Add($"unsupported rules version {version} - this add-on reads version {Version}");

			return new RuleSet([], problems);
		}

		if (!root.TryGetProperty("rules", out JsonElement array) || (array.ValueKind != JsonValueKind.Array)) {
			problems.Add("the rules file has no \"rules\" array");

			return new RuleSet([], problems);
		}

		List<Rule> rules = [];
		HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
		int index = 0;

		foreach (JsonElement element in array.EnumerateArray()) {
			index++;

			if (rules.Count >= MaxRules) {
				problems.Add($"only the first {MaxRules} rules are used");
				break;
			}

			if (element.ValueKind != JsonValueKind.Object) {
				problems.Add($"rule {index}: not an object");
				continue;
			}

			Rule? rule = ParseRule(element, index, problems);

			if (rule == null) {
				continue;
			}

			if (!ids.Add(rule.Id)) {
				problems.Add($"rule {index}: \"{rule.Id}\" is used by another rule - skipped");
				continue;
			}

			rules.Add(rule);
		}

		// Priority first, then the order they were written: a stable, reviewable decision.
		List<Rule> ordered = [.. rules.Select((rule, at) => (rule, at)).OrderBy(static x => x.rule.Priority).ThenBy(static x => x.at).Select(static x => x.rule)];

		return new RuleSet(ordered, problems);
	}

	private static Rule? ParseRule(JsonElement e, int index, List<string> problems) {
		string id = Str(e, "id")?.Trim() is { Length: > 0 } given ? given : $"rule-{index}";
		string kind = (Str(e, "kind") ?? "").Trim().ToLowerInvariant();

		if (!RuleKind.All.Contains(kind)) {
			problems.Add($"rule {index}: unknown kind \"{kind}\"");

			return null;
		}

		string action = (Str(e, "action") ?? "accept").Trim().ToLowerInvariant();

		if (action is not ("accept" or "decline")) {
			problems.Add($"rule {index}: action must be accept or decline");

			return null;
		}

		Rule r = new() {
			Id = id,
			Kind = kind,
			Action = action,
			Enabled = Bool(e, "enabled") ?? true,
			Priority = Int(e, "priority") ?? 100,
			Note = Str(e, "note") ?? "",
			Partners = PartnersOf(e),
			Match = MatchOf(e, index, problems),
			PayIn = PayInOf(e, index, problems)
		};

		return kind switch {
			RuleKind.CardGems => CardGems(e, r, index, problems),
			RuleKind.SetSizeGems => SetSizeGems(e, r, index, problems),
			RuleKind.ItemThreshold => ItemThreshold(e, r, index, problems),
			RuleKind.BoosterValue => BoosterValue(e, r, index, problems),
			RuleKind.FoilSwap => FoilSwap(e, r, index, problems),
			RuleKind.MarketProfit => MarketProfit(e, r, index, problems),
			RuleKind.Expression => ExpressionRule(e, r, index, problems),
			RuleKind.ValueRatio or _ => ValueRatio(e, r, index, problems)
		};
	}

	private static Rule? CardGems(JsonElement e, Rule r, int index, List<string> problems) {
		string worth = (Str(e, "worth") ?? "gems").Trim().ToLowerInvariant();
		string fallback = (Str(e, "fallback") ?? "none").Trim().ToLowerInvariant();

		if (worth is not ("gems" or "wallet")) {
			problems.Add($"rule {index}: worth must be gems or wallet");

			return null;
		}

		if (fallback is not ("none" or "gems" or "wallet") || (fallback == worth)) {
			problems.Add($"rule {index}: fallback must be none, or the other of gems and wallet");
			return null;
		}

		return r with {
			Worth = worth, Fallback = fallback, SameGame = Bool(e, "sameGame") ?? false,
			MarginPct = Number(e, "marginPct"), MinMarginGems = Math.Max(0, Int(e, "minMarginGems") ?? 0)
		};
	}

	private static Rule? SetSizeGems(JsonElement e, Rule r, int index, List<string> problems) {
		string per = (Str(e, "per") ?? "card").Trim().ToLowerInvariant();
		string unknown = (Str(e, "onUnknownSet") ?? "unknown").Trim().ToLowerInvariant();

		if (per is not ("card" or "completeset")) {
			problems.Add($"rule {index}: per must be card or completeSet");
			return null;
		}

		per = per == "completeset" ? "completeSet" : "card";

		if (unknown is not ("unknown" or "abstain")) {
			problems.Add($"rule {index}: onUnknownSet must be unknown or abstain");
			return null;
		}

		Dictionary<int, long> table = [];

		if (e.TryGetProperty("table", out JsonElement t) && (t.ValueKind == JsonValueKind.Object)) {
			foreach (JsonProperty p in t.EnumerateObject()) {
				if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || (size <= 0)
					|| (p.Value.ValueKind != JsonValueKind.Number) || !p.Value.TryGetInt64(out long gems) || (gems <= 0)) {
					problems.Add($"rule {index}: table entry \"{p.Name}\" must be a set size and a whole number of gems above 0");
					return null;
				}

				table[size] = gems;
			}
		}

		if (table.Count > MaxTable) {
			problems.Add($"rule {index}: the table has more than {MaxTable} entries");
			return null;
		}

		long? def = Int(e, "defaultPerCard") is int d ? Math.Max(0, d) : null;
		long? defSet = Int(e, "defaultPerSet") is int s && (s > 0) ? s : null;

		if ((table.Count == 0) && ((per == "completeSet") ? defSet is null : def is null)) {
			problems.Add($"rule {index}: the table is empty, and there's no default for a set size it doesn't list");
			return null;
		}

		return r with {
			Per = per, Table = table, DefaultPerCard = def, DefaultPerSet = defSet, OnUnknownSet = unknown,
			MarginPct = Number(e, "marginPct"), MinMarginGems = Math.Max(0, Int(e, "minMarginGems") ?? 0)
		};
	}

	private static Rule? ItemThreshold(JsonElement e, Rule r, int index, List<string> problems) {
		string target = (Str(e, "target") ?? "").Trim().ToLowerInvariant();
		string mode = (Str(e, "mode") ?? "").Trim().ToLowerInvariant();
		string basis = (Str(e, "basis") ?? "gems").Trim().ToLowerInvariant();

		if (mode is not ("sell" or "buy") || (target != (mode == "sell" ? "giving" : "receiving"))) {
			problems.Add($"rule {index}: target must be {(mode == "sell" ? "giving for a sale" : "receiving for a purchase")}");
			return null;
		}

		if (basis is not ("gems" or "wallet")) {
			problems.Add($"rule {index}: basis must be gems or wallet");
			return null;
		}

		return r with {
			Target = target, Mode = mode, Basis = basis,
			MinEach = Math.Max(0, Int(e, "minEach") ?? 0), MinTotal = Math.Max(0, Int(e, "minTotal") ?? 0),
			MaxEach = Math.Max(0, Int(e, "maxEach") ?? 0), MaxTotal = Math.Max(0, Int(e, "maxTotal") ?? 0)
		};
	}

	private static Rule? BoosterValue(JsonElement e, Rule r, int index, List<string> problems) {
		string target = (Str(e, "target") ?? "").Trim().ToLowerInvariant();
		string mode = (Str(e, "mode") ?? "").Trim().ToLowerInvariant();
		string value = (Str(e, "value") ?? "expected").Trim().ToLowerInvariant();
		string valueIn = (Str(e, "valueIn") ?? "gems").Trim().ToLowerInvariant();
		string floor = (Str(e, "floorMode") ?? "threeCheapestDistinct").Trim().ToLowerInvariant();
		string loose = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant();

		if (mode is not ("sell" or "buy") || (target != (mode == "sell" ? "giving" : "receiving"))) {
			problems.Add($"rule {index}: target must be {(mode == "sell" ? "giving for a sale" : "receiving for a purchase")}");
			return null;
		}

		if (value is not ("expected" or "floor" or "setsize" or "fixed") || valueIn is not ("gems" or "wallet")) {
			problems.Add($"rule {index}: value must be expected, floor, setSize or fixed; valueIn gems or wallet");
			return null;
		}

		if ((value == "fixed") && ((Int(e, "gems") ?? 0) <= 0)) {
			problems.Add($"rule {index}: a fixed booster is worth a whole number of gems above 0");
			return null;
		}

		if (floor is not ("threecheapestdistinct" or "threetimescheapest") || loose is not ("unknown" or "sack")) {
			problems.Add($"rule {index}: floorMode or looseGems isn't one this add-on knows");
			return null;
		}

		return r with {
			Target = target, Mode = mode,
			Value = value == "setsize" ? "setSize" : value,
			ValueIn = valueIn,
			MinMargin = Math.Max(0, Int(e, "minMargin") ?? 0), MinSales = Math.Max(1, Int(e, "minSales") ?? 3),
			MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24), UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99),
			FloorMode = floor == "threetimescheapest" ? "threeTimesCheapest" : "threeCheapestDistinct",
			Gems = Math.Max(0, Int(e, "gems") ?? 0), LooseGems = loose, MarginPct = Number(e, "marginPct")
		};
	}

	private static Rule? FoilSwap(JsonElement e, Rule r, int index, List<string> problems) {
		if (Side(e, "give", index, problems) is not { } give || Side(e, "get", index, problems) is not { } get) {
			return null;
		}

		string mode = (Str(e, "mode") ?? "exact").Trim().ToLowerInvariant();

		if (mode is not ("exact" or "atleast")) {
			problems.Add($"rule {index}: mode must be exact or atLeast");
			return null;
		}

		int? loss = null;

		if (e.TryGetProperty("maxLossPct", out JsonElement lossElement) && (lossElement.ValueKind != JsonValueKind.Null)) {
			if ((lossElement.ValueKind != JsonValueKind.Number) || !lossElement.TryGetInt32(out int pct) || (pct is < 0 or > 100)) {
				problems.Add($"rule {index}: maxLossPct must be 0 to 100, or null");
				return null;
			}

			loss = pct;
		}

		return r with { Give = give, Get = get, SwapMode = mode == "atleast" ? "atLeast" : "exact", MaxLossPct = loss };
	}

	private static SwapSide? Side(JsonElement e, string name, int index, List<string> problems) {
		if (!e.TryGetProperty(name, out JsonElement side) || (side.ValueKind != JsonValueKind.Object)) {
			problems.Add($"rule {index}: {name} is missing");
			return null;
		}

		int count = Int(side, "count") ?? 0;

		if (count < 0) {
			problems.Add($"rule {index}: {name}.count can't be negative");
			return null;
		}

		return new SwapSide(count, Bool(side, "foil") ?? false, MatchOf(side, index, problems));
	}

	private static Rule? MarketProfit(JsonElement e, Rule r, int index, List<string> problems) {
		string price = (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant();
		string loose = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant();

		if (price is not ("conservative" or "lowest" or "median") || loose is not ("unknown" or "sack")) {
			problems.Add($"rule {index}: priceMode or looseGems isn't one this add-on knows");
			return null;
		}

		return r with {
			MinProfitCents = Math.Max(0, Int(e, "minProfitCents") ?? 1), MinProfitPct = Math.Clamp(Number(e, "minProfitPct"), 0, 1_000_000),
			MinSales = Math.Max(1, Int(e, "minSales") ?? 3), MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24),
			PriceMode = price, UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99), LooseGems = loose
		};
	}

	private static Rule? ExpressionRule(JsonElement e, Rule r, int index, List<string> problems) {
		string expr = (Str(e, "expr") ?? Str(e, "expression") ?? "").Trim();

		if (expr.Length == 0) {
			problems.Add($"rule {index}: an expression rule needs \"expr\"");
			return null;
		}

		if (TradeExpression.Validate(expr) is { } why) {
			problems.Add($"rule {index}: {why}");
			return null;
		}

		string price = (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant();
		string loose = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant();

		if (price is not ("conservative" or "lowest" or "median") || loose is not ("unknown" or "sack")) {
			problems.Add($"rule {index}: priceMode or looseGems isn't one this add-on knows");
			return null;
		}

		return r with {
			Expr = expr, MinSales = Math.Max(1, Int(e, "minSales") ?? 3), MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24),
			PriceMode = price, UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99), LooseGems = loose
		};
	}

	private static Rule? ValueRatio(JsonElement e, Rule r, int index, List<string> problems) {
		string basis = (Str(e, "basis") ?? "wallet").Trim().ToLowerInvariant();
		string left = (Str(e, "left") ?? "receiving").Trim().ToLowerInvariant();
		string right = (Str(e, "right") ?? "giving").Trim().ToLowerInvariant();
		string op = (Str(e, "op") ?? ">=").Trim();

		if (basis is not ("gems" or "wallet" or "cardcount" or "foilcount" or "setsize" or "itemcount")) {
			problems.Add($"rule {index}: basis must be gems, wallet, cardCount, foilCount, setSize or itemCount");
			return null;
		}

		if (left is not ("receiving" or "giving") || right is not ("receiving" or "giving")) {
			problems.Add($"rule {index}: left and right must be receiving or giving");
			return null;
		}

		if (op is not (">=" or "<=" or ">" or "<" or "==")) {
			problems.Add($"rule {index}: op must be >=, <=, >, < or ==");
			return null;
		}

		double factor = Number(e, "factor", 1), offset = Number(e, "offset");

		if (!double.IsFinite(factor) || !double.IsFinite(offset)) {
			problems.Add($"rule {index}: factor and offset must be numbers");
			return null;
		}

		string basisName = basis switch {
			"cardcount" => "cardCount",
			"foilcount" => "foilCount",
			"setsize" => "setSize",
			"itemcount" => "itemCount",
			_ => basis
		};

		string price = (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant();

		return r with {
			Basis = basisName, Left = left, Right = right, Op = op, Factor = factor, Offset = offset,
			MinSales = Math.Max(1, Int(e, "minSales") ?? 3), MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24),
			PriceMode = price is "lowest" or "median" ? price : "conservative",
			UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99),
			LooseGems = (Str(e, "looseGems") ?? "sack").Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "unknown" : "sack"
		};
	}

	private static Partners PartnersOf(JsonElement e) {
		if (!e.TryGetProperty("when", out JsonElement when) || (when.ValueKind != JsonValueKind.Object)
			|| !when.TryGetProperty("partners", out JsonElement p) || (p.ValueKind != JsonValueKind.Object)) {
			return Partners.Anyone;
		}

		return new Partners(Lines(p, "allow"), Lines(p, "deny"));
	}

	private static PayIn? PayInOf(JsonElement e, int index, List<string> problems) {
		if (!e.TryGetProperty("payIn", out JsonElement p) || (p.ValueKind == JsonValueKind.Null)) {
			return null;
		}

		if (p.ValueKind != JsonValueKind.Object) {
			problems.Add($"rule {index}: payIn must be an object");
			return null;
		}

		List<SpareKind> kinds = [];

		foreach (string name in Lines(p, "kinds")) {
			if (!Enum.TryParse(name, true, out SpareKind kind) || !Enum.IsDefined(kind)) {
				problems.Add($"rule {index}: payIn kind \"{name}\" isn't one this add-on knows");
				return null;
			}

			kinds.Add(kind);
		}

		return new PayIn(kinds, Bool(p, "only") ?? false);
	}

	private static Match MatchOf(JsonElement e, int index, List<string> problems) {
		if (!e.TryGetProperty("match", out JsonElement m) || (m.ValueKind == JsonValueKind.Null)) {
			return Match.Any;
		}

		if (m.ValueKind != JsonValueKind.Object) {
			problems.Add($"rule {index}: match must be an object");
			return Match.Any;
		}

		List<SpareKind> kinds = [];

		foreach (string name in Lines(m, "kinds")) {
			if (!Enum.TryParse(name, true, out SpareKind kind) || !Enum.IsDefined(kind)) {
				problems.Add($"rule {index}: match kind \"{name}\" isn't one this add-on knows");
				continue;
			}

			kinds.Add(kind);
		}

		List<uint> apps = [];

		foreach (string app in Lines(m, "apps")) {
			if (uint.TryParse(app, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) {
				apps.Add(id);
			} else {
				problems.Add($"rule {index}: app \"{app}\" isn't a number");
			}
		}

		List<string> hashes = [.. Lines(m, "hashes").Take(MaxNames)];
		List<string> names = [.. Lines(m, "names").Take(MaxNames)];

		if (hashes.Count == MaxNames) {
			problems.Add($"rule {index}: only the first {MaxNames} hashes are used");
		}

		if (names.Count == MaxNames) {
			problems.Add($"rule {index}: only the first {MaxNames} names are used");
		}

		bool? foil = m.TryGetProperty("foil", out JsonElement f) && (f.ValueKind is JsonValueKind.True or JsonValueKind.False) ? f.GetBoolean() : null;

		return new Match(kinds, apps, hashes, names, foil, Int(m, "minItems"), Int(m, "maxItems"));
	}

	private static List<string> Lines(JsonElement e, string name) {
		if (!e.TryGetProperty(name, out JsonElement v)) {
			return [];
		}

		if (v.ValueKind == JsonValueKind.Array) {
			return [.. v.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.String).Select(static x => x.GetString() ?? "").Where(static x => x.Length > 0)];
		}

		return v.ValueKind == JsonValueKind.String
			? [.. (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
			: [];
	}

	private static string? Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() : null;

	private static bool? Bool(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : null;

	private static int? Int(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetInt32(out int n) ? n : null;

	private static double Number(JsonElement e, string name, double fallback = 0) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : fallback;

	// -- writing ----------------------------------------------------------------
	private static void WriteRule(Utf8JsonWriter w, Rule r) {
		w.WriteStartObject();
		w.WriteString("id", r.Id);
		w.WriteString("kind", r.Kind);
		w.WriteString("action", r.Action);
		w.WriteBoolean("enabled", r.Enabled);
		w.WriteNumber("priority", r.Priority);

		if (r.Note.Length > 0) {
			w.WriteString("note", r.Note);
		}

		if ((r.Partners.Allow.Count > 0) || (r.Partners.Deny.Count > 0)) {
			w.WriteStartObject("when");
			w.WriteStartObject("partners");
			WriteArray(w, "allow", r.Partners.Allow);
			WriteArray(w, "deny", r.Partners.Deny);
			w.WriteEndObject();
			w.WriteEndObject();
		}

		if (!Same(r.Match, Match.Any)) {
			w.WriteStartObject("match");
			WriteArray(w, "kinds", [.. r.Match.Kinds.Select(static k => k.ToString())]);
			WriteArray(w, "apps", [.. r.Match.Apps.Select(static a => a.ToString(CultureInfo.InvariantCulture))]);
			WriteArray(w, "hashes", r.Match.Hashes);
			WriteArray(w, "names", r.Match.Names);

			if (r.Match.Foil is bool foil) {
				w.WriteBoolean("foil", foil);
			}

			if (r.Match.MinItems is int min) {
				w.WriteNumber("minItems", min);
			}

			if (r.Match.MaxItems is int max) {
				w.WriteNumber("maxItems", max);
			}

			w.WriteEndObject();
		}

		if (r.PayIn is { } pay) {
			w.WriteStartObject("payIn");
			WriteArray(w, "kinds", [.. pay.Kinds.Select(static k => k.ToString())]);
			w.WriteBoolean("only", pay.Only);
			w.WriteEndObject();
		}

		switch (r.Kind) {
			case RuleKind.CardGems:
				w.WriteString("worth", r.Worth);
				w.WriteString("fallback", r.Fallback);
				w.WriteBoolean("sameGame", r.SameGame);
				w.WriteNumber("marginPct", r.MarginPct);
				w.WriteNumber("minMarginGems", r.MinMarginGems);

				break;
			case RuleKind.SetSizeGems:
				w.WriteString("per", r.Per);
				w.WriteStartObject("table");

				foreach ((int size, long gems) in r.Table.OrderBy(static t => t.Key)) {
					w.WriteNumber(size.ToString(CultureInfo.InvariantCulture), gems);
				}

				w.WriteEndObject();

				if (r.DefaultPerCard is long def) {
					w.WriteNumber("defaultPerCard", def);
				}

				if (r.DefaultPerSet is long defSet) {
					w.WriteNumber("defaultPerSet", defSet);
				}

				w.WriteString("onUnknownSet", r.OnUnknownSet);
				w.WriteNumber("marginPct", r.MarginPct);
				w.WriteNumber("minMarginGems", r.MinMarginGems);

				break;
			case RuleKind.ItemThreshold:
				w.WriteString("target", r.Target);
				w.WriteString("mode", r.Mode);
				w.WriteString("basis", r.Basis);
				w.WriteNumber(r.Mode == "buy" ? "maxEach" : "minEach", r.Mode == "buy" ? r.MaxEach : r.MinEach);
				w.WriteNumber(r.Mode == "buy" ? "maxTotal" : "minTotal", r.Mode == "buy" ? r.MaxTotal : r.MinTotal);

				break;
			case RuleKind.BoosterValue:
				w.WriteString("target", r.Target);
				w.WriteString("mode", r.Mode);
				w.WriteString("value", r.Value);
				w.WriteString("valueIn", r.ValueIn);
				w.WriteNumber("marginPct", r.MarginPct);
				w.WriteNumber("minMargin", r.MinMargin);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("floorMode", r.FloorMode);

				if (r.Value == "fixed") {
					w.WriteNumber("gems", r.Gems);
				}

				if (r.ValueIn == "wallet") {
					w.WriteString("looseGems", r.LooseGems);
				}

				break;
			case RuleKind.FoilSwap:
				WriteSide(w, "give", r.Give!);
				WriteSide(w, "get", r.Get!);
				w.WriteString("mode", r.SwapMode);

				if (r.MaxLossPct is int loss) {
					w.WriteNumber("maxLossPct", loss);
				} else {
					w.WriteNull("maxLossPct");
				}

				break;
			case RuleKind.MarketProfit:
				w.WriteNumber("minProfitCents", r.MinProfitCents);
				w.WriteNumber("minProfitPct", r.MinProfitPct);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteString("priceMode", r.PriceMode);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("looseGems", r.LooseGems);

				break;
			case RuleKind.Expression:
				w.WriteString("expr", r.Expr);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteString("priceMode", r.PriceMode);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("looseGems", r.LooseGems);

				break;
			default:
				w.WriteString("basis", r.Basis);
				w.WriteString("left", r.Left);
				w.WriteString("right", r.Right);
				w.WriteString("op", r.Op);
				w.WriteNumber("factor", r.Factor);
				w.WriteNumber("offset", r.Offset);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteString("priceMode", r.PriceMode);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("looseGems", r.LooseGems);

				break;
		}

		w.WriteEndObject();
	}

	private static void WriteSide(Utf8JsonWriter w, string name, SwapSide side) {
		w.WriteStartObject(name);
		w.WriteNumber("count", side.Count);
		w.WriteBoolean("foil", side.Foil);

		if (!Same(side.Match, Match.Any)) {
			w.WriteStartObject("match");
			WriteArray(w, "kinds", [.. side.Match.Kinds.Select(static k => k.ToString())]);
			WriteArray(w, "apps", [.. side.Match.Apps.Select(static a => a.ToString(CultureInfo.InvariantCulture))]);
			WriteArray(w, "hashes", side.Match.Hashes);
			WriteArray(w, "names", side.Match.Names);
			w.WriteEndObject();
		}

		w.WriteEndObject();
	}

	private static void WriteArray(Utf8JsonWriter w, string name, IReadOnlyList<string> values) {
		w.WriteStartArray(name);

		foreach (string v in values) {
			w.WriteStringValue(v);
		}

		w.WriteEndArray();
	}

	private static bool Same(Match a, Match b) =>
		a.Kinds.SequenceEqual(b.Kinds) && a.Apps.SequenceEqual(b.Apps) && a.Hashes.SequenceEqual(b.Hashes) && a.Names.SequenceEqual(b.Names)
		&& (a.Foil == b.Foil) && (a.MinItems == b.MinItems) && (a.MaxItems == b.MaxItems);
}
