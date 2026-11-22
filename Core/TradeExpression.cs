using System.Globalization;
using System.Text;

namespace GamerFarm.Addons.TradeRules.Core;

/// <summary>
/// A user-written condition over a trade: the escape hatch for a rule the typed kinds don't cover.
/// </summary>
/// <remarks>
/// A small, sandboxed formula language - no reflection, no <c>eval</c>, no loops of the user's making. It compares the two
/// sides of an offer and can aggregate their items:
/// <code>
/// receiving.gems &gt;= giving.gems * 1.15
/// count("giving", "Card", 730) == 3 &amp;&amp; count("receiving", "FoilCard", 730) == 1
/// min("giving", "wallet", "Card") &gt; 10 &amp;&amp; sum("receiving", "wallet") &gt;= sum("giving", "wallet")
/// </code>
/// A value that isn't known - a card with no fresh quote, a gem with no GooValue, a loose gem with no sack price - makes
/// whatever uses it unknown, and an unknown condition leaves the offer alone. It is never read as zero.
/// </remarks>
public sealed class TradeExpression {
	/// <summary>The longest expression accepted. A rule is read once a look, but a tiny bound keeps a bad paste cheap.</summary>
	public const int MaxLength = 2000;

	private static readonly HashSet<string> Sides = new(StringComparer.Ordinal) { "giving", "receiving" };

	private static readonly HashSet<string> Variables = new(StringComparer.Ordinal) {
		"giving.gems", "receiving.gems",
		"giving.wallet", "receiving.wallet",
		"giving.count", "receiving.count",
		"giving.items", "receiving.items",
		"giving.cards", "receiving.cards",
		"giving.foils", "receiving.foils",
		"giving.boosters", "receiving.boosters",
		"giving.loosegems", "receiving.loosegems",
		"giving.sacks", "receiving.sacks",
		"giving.games", "receiving.games",
		"giving.sets", "receiving.sets",
		"giving.priced", "receiving.priced",
		"profit.gems", "profit.wallet"
	};

	private static readonly IReadOnlyDictionary<string, (int Min, int Max)> Functions = new Dictionary<string, (int, int)>(StringComparer.Ordinal) {
		["count"] = (1, 3),
		["has"] = (1, 3),
		["sum"] = (2, 4),
		["min"] = (2, 4),
		["max"] = (2, 4),
		["avg"] = (2, 4),
		["samegame"] = (0, 0)
	};

	private readonly Node _root;

	private TradeExpression(string text, Node root) {
		Text = text;
		_root = root;
	}

	/// <summary>The expression as written.</summary>
	public string Text { get; }

	/// <summary>Parse one. False and a problem when it isn't valid - the codec uses this to reject a rule on save.</summary>
	public static bool TryParse(string? text, out TradeExpression? expression, out string? problem) {
		expression = null;
		problem = null;

		if (string.IsNullOrWhiteSpace(text)) {
			problem = "the expression is empty";
			return false;
		}

		if (text.Length > MaxLength) {
			problem = $"the expression is longer than {MaxLength} characters";
			return false;
		}

		try {
			Parser parser = new(text);
			expression = new TradeExpression(text.Trim(), parser.Parse());

			return true;
		} catch (ExpressionProblem e) {
			problem = e.Message;
			return false;
		}
	}

	/// <summary>Why the text isn't a valid expression, or null when it is.</summary>
	public static string? Validate(string? text) => TryParse(text, out _, out string? problem) ? null : problem;

	/// <summary>Evaluate it. Null when the answer depends on something unknown; otherwise 0 is false and anything else true.</summary>
	public async Task<double?> EvaluateAsync(TradeExpressionContext context, CancellationToken ct) =>
		await _root.EvalAsync(context, ct).ConfigureAwait(false);

	// -- the tree --------------------------------------------------------------
	private abstract class Node {
		public abstract Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct);
	}

	private sealed class NumberNode(double value) : Node {
		public double Number { get; } = value;

		public override Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct) => Task.FromResult<double?>(Number);
	}

	private sealed class TextNode(string value) : Node {
		public string Value { get; } = value;

		public override Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct) => Task.FromResult<double?>(null);
	}

	private sealed class VariableNode(string name) : Node {
		public string Name { get; } = name;

		public override Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct) => VariableAsync(Name, context, ct);
	}

	private sealed class UnaryNode(char op, Node operand) : Node {
		public override async Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct) {
			double? value = await operand.EvalAsync(context, ct).ConfigureAwait(false);

			return value is double v ? op == '-' ? -v : v : null;
		}
	}

	private sealed class BinaryNode(string op, Node left, Node right) : Node {
		public override async Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct) {
			if (op is "&&" or "||") {
				double? first = await left.EvalAsync(context, ct).ConfigureAwait(false);

				if (op == "&&") {
					if (first is double a && a == 0) {
						return 0;
					}

					double? second = await right.EvalAsync(context, ct).ConfigureAwait(false);

					return first is null ? (second is double s && s == 0 ? 0 : null) : second is double b ? (b == 0 ? 0 : 1) : null;
				}

				if (first is double t && t != 0) {
					return 1;
				}

				double? other = await right.EvalAsync(context, ct).ConfigureAwait(false);

				return first is null ? (other is double o && o != 0 ? 1 : null) : other is double c ? (c == 0 ? 0 : 1) : null;
			}

			double? l = await left.EvalAsync(context, ct).ConfigureAwait(false);
			double? r = await right.EvalAsync(context, ct).ConfigureAwait(false);

			if (l is not double x || r is not double y) {
				return null;
			}

			return op switch {
				"+" => x + y,
				"-" => x - y,
				"*" => x * y,
				"/" => Math.Abs(y) < double.Epsilon ? null : x / y,
				">=" => x >= y ? 1 : 0,
				"<=" => x <= y ? 1 : 0,
				">" => x > y ? 1 : 0,
				"<" => x < y ? 1 : 0,
				"==" => Math.Abs(x - y) < 1e-9 ? 1 : 0,
				"!=" => Math.Abs(x - y) < 1e-9 ? 0 : 1,
				_ => null
			};
		}
	}

	private sealed class CallNode(string name, IReadOnlyList<Node> arguments) : Node {
		public override async Task<double?> EvalAsync(TradeExpressionContext context, CancellationToken ct) {
			string Side(int at) => ((TextNode) arguments[at]).Value;

			switch (name) {
				case "samegame":
					return SameGame(context.Values);

				case "count" or "has": {
					IReadOnlyList<RuleItem> side = Items(context.Values, Side(0));
					string? kind = arguments.Count > 1 ? ((TextNode) arguments[1]).Value : null;
					uint app = arguments.Count > 2 ? (uint) ((NumberNode) arguments[2]).Number : 0;
					double count = side.Where(i => Matches(i, kind, app)).Sum(static i => (double) i.Count);

					return name == "count" ? count : count > 0 ? 1 : 0;
				}

				default: {
					IReadOnlyList<RuleItem> side = Items(context.Values, Side(0));
					string basis = ((TextNode) arguments[1]).Value;
					string? kind = arguments.Count > 2 ? ((TextNode) arguments[2]).Value : null;
					uint app = arguments.Count > 3 ? (uint) ((NumberNode) arguments[3]).Number : 0;
					List<double> values = [];

					foreach (RuleItem item in side.Where(i => Matches(i, kind, app))) {
						ValuedItem valued = basis == "wallet"
							? await context.WalletAsync(item, Side(0) == "giving", ct).ConfigureAwait(false)
							: await context.GemsAsync(item, ct).ConfigureAwait(false);
						int? value = basis == "wallet" ? valued.WalletCents : valued.Gems;

						if (value is not int known) {
							return null;   // one unpriced item makes the total unknown, never zero
						}

						values.Add(known * (double) item.Count);
					}

					return name switch {
						"sum" => values.Sum(),
						"min" => values.Count == 0 ? null : values.Min(),
						"max" => values.Count == 0 ? null : values.Max(),
						"avg" => values.Count == 0 ? null : values.Average(),
						_ => null
					};
				}
			}
		}
	}

	private static IReadOnlyList<RuleItem> Items(TradeValues values, string side) => side == "giving" ? values.Giving : values.Receiving;

	private static bool Matches(RuleItem item, string? kind, uint app) =>
		(kind == null || item.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase)) && (app == 0 || item.Game == app);

	private static double SameGame(TradeValues values) {
		HashSet<uint> games = [.. values.Giving.Select(static i => i.Game).Concat(values.Receiving.Select(static i => i.Game)).Where(static g => g != 0)];

		return games.Count <= 1 ? 1 : 0;
	}

	private static async Task<double?> VariableAsync(string name, TradeExpressionContext context, CancellationToken ct) {
		TradeValues values = context.Values;
		int dot = name.IndexOf('.', StringComparison.Ordinal);
		string first = name[..dot], second = name[(dot + 1)..];

		if (first == "profit") {
			long? giving = await TotalAsync(values, "giving", second, context, ct).ConfigureAwait(false);
			long? receiving = await TotalAsync(values, "receiving", second, context, ct).ConfigureAwait(false);

			return giving is long g && receiving is long r ? r - g : null;
		}

		IReadOnlyList<RuleItem> side = Items(values, first);

		return second switch {
			"count" or "items" => side.Sum(static i => (double) i.Count),
			"cards" => side.Where(static i => i.Kind == SpareKind.Card).Sum(static i => (double) i.Count),
			"foils" => side.Where(static i => i.Kind == SpareKind.FoilCard).Sum(static i => (double) i.Count),
			"boosters" => side.Where(static i => i.Kind == SpareKind.BoosterPack).Sum(static i => (double) i.Count),
			"loosegems" => side.Where(static i => i.Kind == SpareKind.Gems).Sum(static i => (double) i.Count),
			"sacks" => side.Where(static i => i.Kind == SpareKind.GemSack).Sum(static i => (double) i.Count),
			"games" => side.Select(static i => i.Game).Where(static g => g != 0).Distinct().Count(),
			"priced" => await values.GemsOfAsync(side, ct).ConfigureAwait(false) is not null
				&& await values.WalletOfAsync(side, first == "giving", context.MinSales, context.MaxAgeHours, context.UndercutPct, context.LooseGems, ct).ConfigureAwait(false) is not null ? 1 : 0,
			"gems" => await TotalAsync(values, first, "gems", context, ct).ConfigureAwait(false) is long g ? g : null,
			"wallet" => await TotalAsync(values, first, "wallet", context, ct).ConfigureAwait(false) is long w ? w : null,
			"sets" => await SetsAsync(values, side, ct).ConfigureAwait(false),
			_ => null
		};
	}

	private static Task<long?> TotalAsync(TradeValues values, string side, string basis, TradeExpressionContext context, CancellationToken ct) {
		IReadOnlyList<RuleItem> items = Items(values, side);

		return basis switch {
			"gems" => values.GemsOfAsync(items, ct),
			"wallet" => values.WalletOfAsync(items, side == "giving", context.MinSales, context.MaxAgeHours, context.UndercutPct, context.LooseGems, ct),
			_ => Task.FromResult<long?>(null)
		};
	}

	private static async Task<double?> SetsAsync(TradeValues values, IReadOnlyList<RuleItem> side, CancellationToken ct) {
		double sets = 0;

		foreach (IGrouping<uint, RuleItem> game in side.Where(static i => i.Kind == SpareKind.Card).GroupBy(static i => i.Game)) {
			CardSetInfo? set = await values.SetOfAsync(game.Key, ct).ConfigureAwait(false);

			if (set is not { SetSize: > 0 }) {
				return null;
			}

			Dictionary<string, int> held = new(StringComparer.Ordinal);

			foreach (RuleItem item in game) {
				held[item.Hash] = held.GetValueOrDefault(item.Hash) + item.Count;
			}

			if (set.Cards.Count > 0 && set.Cards.All(c => held.GetValueOrDefault(c) > 0)) {
				sets += set.Cards.Min(c => held.GetValueOrDefault(c));
			} else {
				// The plugin API gives the set's size but not its card hashes. A size-only answer is still useful: a
				// complete set of this size is one set when the side holds exactly that many different cards.
				if (game.Select(static i => i.Hash).Distinct(StringComparer.Ordinal).Count() == set.SetSize && game.Sum(static i => i.Count) == set.SetSize) {
					sets++;
				} else {
					return null;
				}
			}
		}

		return sets;
	}

	// -- the parser ------------------------------------------------------------
	private sealed class ExpressionProblem(string message) : Exception(message);

	private sealed class Parser(string text) {
		private int _at;

		public Node Parse() {
			Node node = Or();

			Skip();

			if (_at < text.Length) {
				throw new ExpressionProblem($"unexpected '{text[_at]}' at position {_at + 1}");
			}

			return node;
		}

		private Node Or() {
			Node left = And();

			while (Take("||")) {
				left = new BinaryNode("||", left, And());
			}

			return left;
		}

		private Node And() {
			Node left = Compare();

			while (Take("&&")) {
				left = new BinaryNode("&&", left, Compare());
			}

			return left;
		}

		private Node Compare() {
			Node left = Sum();

			foreach (string op in new[] { ">=", "<=", "==", "!=", ">", "<" }) {
				if (Take(op)) {
					return new BinaryNode(op, left, Sum());
				}
			}

			return left;
		}

		private Node Sum() {
			Node left = Term();

			while (true) {
				if (Take("+")) {
					left = new BinaryNode("+", left, Term());
				} else if (Take("-")) {
					left = new BinaryNode("-", left, Term());
				} else {
					return left;
				}
			}
		}

		private Node Term() {
			Node left = Unary();

			while (true) {
				if (Take("*")) {
					left = new BinaryNode("*", left, Unary());
				} else if (Take("/")) {
					left = new BinaryNode("/", left, Unary());
				} else {
					return left;
				}
			}
		}

		private Node Unary() {
			Skip();

			if (Take("-")) {
				return new UnaryNode('-', Unary());
			}

			if (Take("+")) {
				return Unary();
			}

			return Primary();
		}

		private Node Primary() {
			Skip();

			if (_at >= text.Length) {
				throw new ExpressionProblem("the expression ends too soon");
			}

			char c = text[_at];

			if (char.IsAsciiDigit(c) || ((c == '.') && (_at + 1 < text.Length) && char.IsAsciiDigit(text[_at + 1]))) {
				return new NumberNode(Number());
			}

			if (c == '"') {
				return new TextNode(Quoted());
			}

			if (c == '(') {
				_at++;
				Node inner = Or();
				Skip();

				if (!Take(")")) {
					throw new ExpressionProblem("a ')' is missing");
				}

				return inner;
			}

			if (char.IsAsciiLetter(c) || (c == '_')) {
				string name = Word();

				Skip();

				if (Take("(")) {
					return Call(name);
				}

				string key = name.ToLowerInvariant();

				if (!Variables.Contains(key)) {
					throw new ExpressionProblem($"'{name}' isn't a value this can read");
				}

				return new VariableNode(key);
			}

			throw new ExpressionProblem($"unexpected '{c}' at position {_at + 1}");
		}

		private Node Call(string name) {
			string key = name.ToLowerInvariant();

			if (!Functions.TryGetValue(key, out (int Min, int Max) arity)) {
				throw new ExpressionProblem($"'{name}' isn't a function this can use");
			}

			List<Node> arguments = [];
			Skip();

			if (!Take(")")) {
				do {
					arguments.Add(Or());
					Skip();
				} while (Take(","));

				if (!Take(")")) {
					throw new ExpressionProblem($"a ')' is missing after {name}(");
				}
			}

			if ((arguments.Count < arity.Min) || (arguments.Count > arity.Max)) {
				throw new ExpressionProblem($"'{name}' takes {(arity.Min == arity.Max ? arity.Min.ToString(CultureInfo.InvariantCulture) : $"{arity.Min} to {arity.Max}")} value(s)");
			}

			if (key == "samegame") {
				return new CallNode(key, arguments);
			}

			if (arguments[0] is not TextNode side || !Sides.Contains(side.Value.Trim().ToLowerInvariant())) {
				throw new ExpressionProblem($"{name}'s first value must be \"giving\" or \"receiving\"");
			}

			arguments[0] = new TextNode(side.Value.Trim().ToLowerInvariant());

			if (key is "count" or "has") {
				if ((arguments.Count > 1) && (arguments[1] is not TextNode)) {
					throw new ExpressionProblem($"{name}'s second value must be a kind in quotes, like \"Card\"");
				}
			} else {
				if (arguments[1] is not TextNode basis) {
					throw new ExpressionProblem($"{name}'s second value must be \"gems\" or \"wallet\"");
				}

				string unit = basis.Value.Trim().ToLowerInvariant();

				if (unit is not ("gems" or "wallet")) {
					throw new ExpressionProblem($"{name}'s second value must be \"gems\" or \"wallet\"");
				}

				arguments[1] = new TextNode(unit);

				if ((arguments.Count > 2) && (arguments[2] is not TextNode)) {
					throw new ExpressionProblem($"{name}'s third value must be a kind in quotes, like \"Card\"");
				}
			}

			int appAt = key is "count" or "has" ? 2 : 3;

			if ((arguments.Count > appAt) && (arguments[appAt] is not NumberNode)) {
				throw new ExpressionProblem($"{name}'s last value must be a game's app id");
			}

			return new CallNode(key, arguments);
		}

		private double Number() {
			int start = _at;

			while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || (text[_at] == '.'))) {
				_at++;
			}

			if (!double.TryParse(text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) {
				throw new ExpressionProblem($"'{text[start.._at]}' isn't a number");
			}

			return value;
		}

		private string Quoted() {
			_at++;
			StringBuilder quoted = new();

			while (_at < text.Length) {
				char c = text[_at++];

				if (c == '"') {
					return quoted.ToString();
				}

				if ((c == '\\') && (_at < text.Length)) {
					quoted.Append(text[_at++]);
					continue;
				}

				quoted.Append(c);
			}

			throw new ExpressionProblem("a quote is missing its closing '\"'");
		}

		private string Word() {
			int start = _at;

			while (_at < text.Length) {
				char c = text[_at];

				if (char.IsAsciiLetterOrDigit(c) || (c == '_') || (c == '.')) {
					_at++;
				} else {
					break;
				}
			}

			return text[start.._at];
		}

		private bool Take(string token) {
			Skip();

			if (!text.AsSpan(_at).StartsWith(token, StringComparison.Ordinal)) {
				return false;
			}

			_at += token.Length;
			return true;
		}

		private void Skip() {
			while ((_at < text.Length) && char.IsWhiteSpace(text[_at])) {
				_at++;
			}
		}
	}
}

/// <summary>What an expression reads: the offer's values, and the wallet options a user's rule sets.</summary>
public sealed class TradeExpressionContext(TradeValues values, int minSales, int maxAgeHours, double undercutPct, string looseGems) {
	private readonly Dictionary<(ulong Asset, bool Giving), Task<ValuedItem>> _gems = [];
	private readonly Dictionary<(ulong Asset, bool Giving), Task<ValuedItem>> _wallet = [];

	/// <summary>The offer being valued.</summary>
	public TradeValues Values { get; } = values;

	/// <summary>Minimum sales behind a price (the plugin API has no count; kept for the preview and future use).</summary>
	public int MinSales { get; } = Math.Max(1, minSales);

	/// <summary>Oldest price accepted, in hours.</summary>
	public int MaxAgeHours { get; } = Math.Max(1, maxAgeHours);

	/// <summary>How far under the cheapest listing to value a sale.</summary>
	public double UndercutPct { get; } = Math.Clamp(undercutPct, 0, 99);

	/// <summary>Loose gems as a share of a sack, or unpriced.</summary>
	public string LooseGems { get; } = looseGems;

	/// <summary>One item's gem value, cached per offer.</summary>
	public Task<ValuedItem> GemsAsync(RuleItem item, CancellationToken ct) {
		lock (_gems) {
			if (!_gems.TryGetValue((item.AssetId, false), out Task<ValuedItem>? task)) {
				_gems[(item.AssetId, false)] = task = Values.GemsAsync(item, ct);
			}

			return task;
		}
	}

	/// <summary>One item's wallet value, cached per offer and side.</summary>
	public Task<ValuedItem> WalletAsync(RuleItem item, bool giving, CancellationToken ct) {
		lock (_wallet) {
			if (!_wallet.TryGetValue((item.AssetId, giving), out Task<ValuedItem>? task)) {
				_wallet[(item.AssetId, giving)] = task = Values.WalletAsync(item, giving, MinSales, MaxAgeHours, UndercutPct, LooseGems, ct);
			}

			return task;
		}
	}
}
