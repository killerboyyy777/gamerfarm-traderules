using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace GamerFarm.Addons.TradeRules;

/// <summary>
/// The add-on's own translations: lang/&lt;code&gt;.json inside the DLL, English text to translated, in the app's language.
/// English is the key, so a missing one stays English.
/// </summary>
internal static class Texts {
	/// <summary>The language to translate into, read at each call so a change follows at once.</summary>
	public static Func<string> Language { get; set; } = static () => "en";

	private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyDictionary<string, string> Pack(string code) =>
		Packs.GetOrAdd(code, static c => {
			if (c.Equals("en", StringComparison.OrdinalIgnoreCase)) {
				return new Dictionary<string, string>();
			}

			try {
				using Stream? s = typeof(Texts).Assembly.GetManifestResourceStream($"lang.{c}.json");

				return s == null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
			} catch (JsonException) {
				return new Dictionary<string, string>();
			}
		});

	/// <summary>English kept as it is - a reason stored on disk.</summary>
	public static string R(string english) => english;

	/// <summary>The English, translated, with {0}, {1} filled in invariantly.</summary>
	public static string T(string english, params object?[] args) {
		string text = Pack(Language()).TryGetValue(english, out string? t) && (t.Length > 0) ? t : english;

		if (args.Length == 0) {
			return text;
		}

		try {
			return string.Format(CultureInfo.InvariantCulture, text, args);
		} catch (FormatException) {
			return string.Format(CultureInfo.InvariantCulture, english, args);
		}
	}
}
