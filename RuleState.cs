using System.Text.Json;

namespace GamerFarm.Addons.TradeRules;

/// <summary>One offer's answer, kept only so the dashboard can show what the add-on last said.</summary>
public sealed record LastAnswer(string Account, ulong OfferId, string Verdict, string RuleId, string Why) {
	/// <summary>One line for the page.</summary>
	public string Line => $"#{OfferId}  {Verdict}" + (RuleId.Length > 0 ? $"  [{RuleId}]" : "") + (Why.Length > 0 ? $"  - {Why}" : "");
}

/// <summary>
/// Everything the add-on keeps between restarts: recent answers, and when an offer last had its prices read again. Saved as
/// one JSON file through the host (config/plugins/traderules.json).
/// </summary>
public sealed class RuleState {
	private const int KeepAnswers = 20;

	/// <summary>Recent answers, newest first, capped.</summary>
	public List<LastAnswer> Answers { get; set; } = [];

	/// <summary>Account and offer id -> UTC ticks of the last price refresh.</summary>
	public Dictionary<string, long> RefreshedAt { get; set; } = new(StringComparer.Ordinal);

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

	/// <summary>Read what was saved. Null text is a first run; text that can't be read is an empty state, never a crash.</summary>
	public static RuleState Read(string? text) {
		if (string.IsNullOrWhiteSpace(text)) {
			return new RuleState();
		}

		try {
			RuleState? read = JsonSerializer.Deserialize<RuleState>(text, Json);

			return read == null ? new RuleState() : new RuleState {
				Answers = [.. (read.Answers ?? []).Where(static a => a != null).Take(KeepAnswers)],
				RefreshedAt = read.RefreshedAt ?? new Dictionary<string, long>(StringComparer.Ordinal)
			};
		} catch (JsonException) {
			return new RuleState();
		}
	}

	/// <summary>Remember an answer, dropping the oldest.</summary>
	public void Note(LastAnswer answer) {
		Answers.Insert(0, answer);

		if (Answers.Count > KeepAnswers) {
			Answers.RemoveRange(KeepAnswers, Answers.Count - KeepAnswers);
		}
	}

	/// <summary>Serialize for the host.</summary>
	public string Write() {
		if (Answers.Count > KeepAnswers) {
			Answers.RemoveRange(KeepAnswers, Answers.Count - KeepAnswers);
		}

		return JsonSerializer.Serialize(this, Json);
	}
}
