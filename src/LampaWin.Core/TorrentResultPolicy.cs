using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LampaWin.Core;

/// <summary>Rejects clearly wrong search results and ranks viable torrents by swarm health.</summary>
public static class TorrentResultPolicy
{
    private static readonly string[] GameMarkers =
    [
        "fitgirl", "steam rip", "steamrip", "xatab", "igruha", "early access",
        "nintendo switch", "playstation 4", "playstation 5", "xbox 360",
        "игры на пк", "игра для пк", "pc repack", "gog repack", " ps4 ", " ps5 "
    ];

    private static readonly HashSet<string> IgnoredQueryWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "film", "movie", "season", "series",
        "фильм", "сериал", "сезон", "серия"
    };

    public static void FilterAndRank(JsonArray items, string? query, int? releaseYear = null, bool isSeries = false, bool includeUncertain = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        var queryTokens = Tokens(query).Where(x => !IgnoredQueryWords.Contains(x)
            && !Regex.IsMatch(x, @"^s\d{1,2}(?:e\d{1,3})?$", RegexOptions.IgnoreCase)).ToHashSet(StringComparer.Ordinal);
        // A numeric movie name such as "1917" or "300" must not turn the
        // relevance check into an unfiltered search. The first number is the title.
        if (queryTokens.Count == 0)
        {
            var numericTitle = Regex.Match(query ?? string.Empty, @"^\s*(\d{3,4})\b");
            if (numericTitle.Success) queryTokens.Add(numericTitle.Groups[1].Value);
        }
        var candidates = items.Where(x => x is JsonObject).Cast<JsonObject>()
            .Where(x => !IsGame(x))
            .Where(x => includeUncertain || IsRelevant(x, queryTokens, query, releaseYear, isSeries))
            .ToList();

        // Zero-seed results cannot start reliably. Keep them only when Jackett returned no
        // demonstrably healthy alternative; missing seed information is not treated as zero.
        if (!includeUncertain && candidates.Any(x => Seeders(x) >= 3))
            candidates.RemoveAll(x => Seeders(x) == 0);

        candidates.Sort((left, right) => Score(right).CompareTo(Score(left)));
        items.Clear();
        foreach (var candidate in candidates) items.Add(candidate);
    }

    private static bool IsGame(JsonObject item)
    {
        if (item["Category"] is JsonArray categories && categories.Any(IsGameCategory)) return true;
        var text = $"{Text(item, "Title")} {Text(item, "Description")} {Text(item, "Details")} {Text(item, "CategoryDesc")}";
        var normalized = Normalize(text);
        return GameMarkers.Any(normalized.Contains)
            || normalized.Contains(" repack ") && (normalized.Contains(" pc ") || normalized.Contains(" windows "));
    }

    private static bool IsGameCategory(JsonNode? node)
    {
        if (node is null) return false;
        var value = node.ToString();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var category)) return false;
        return category is >= 1000 and < 2000 or >= 4000 and < 5000;
    }

    private static bool IsRelevant(JsonObject item, HashSet<string> queryTokens, string? query, int? releaseYear, bool isSeries)
    {
        if (queryTokens.Count == 0) return true;
        var title = Text(item, "Title");
        var titleTokens = Tokens(title).ToHashSet(StringComparer.Ordinal);
        foreach (Match number in Regex.Matches(title, @"\b\d{3,4}\b")) titleTokens.Add(number.Value);
        var required = queryTokens.Count == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(queryTokens.Count * .75));
        if (queryTokens.Count(titleTokens.Contains) < required) return false;
        // Indexers can match words inside subtitles of completely different films.
        // Require identity at the start of a title or translated alias, after group tags.
        var identity = Tokens(query).FirstOrDefault(queryTokens.Contains) ?? queryTokens.First();
        if (!Regex.Split(title, @"[/|]").Any(alias =>
        {
            var withoutGroup = Regex.Replace(alias.Trim(), @"^(?:\[[^\]]+\]\s*)+", string.Empty);
            var words = Normalize(withoutGroup).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !IgnoredQueryWords.Contains(word) && word is not ("a" or "an"));
            return words.FirstOrDefault() == identity;
        })) return false;
        var season = Regex.Match(query ?? string.Empty, @"\b(?:s|season\s*|сезон\s*)(\d{1,2})", RegexOptions.IgnoreCase);
        if (season.Success)
        {
            var requestedSeason = int.Parse(season.Groups[1].Value, CultureInfo.InvariantCulture);
            var seasons = Regex.Matches(title, @"\b(?:s|season\s*|сезон(?:ы|ов)?\s*)(\d{1,2})(?:\s*[-–]\s*s?(\d{1,2}))?", RegexOptions.IgnoreCase);
            if (seasons.Count > 0 && !seasons.Any(match =>
            {
                var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : first;
                return requestedSeason >= first && requestedSeason <= last;
            })) return false;
        }
        var episode = Regex.Match(query ?? string.Empty, @"\b(?:s\d{1,2})?e(\d{1,3})\b|\b(?:episode|серия)\s*(\d{1,3})\b", RegexOptions.IgnoreCase);
        if (episode.Success)
        {
            var requestedEpisode = int.Parse(episode.Groups[1].Success ? episode.Groups[1].Value : episode.Groups[2].Value, CultureInfo.InvariantCulture);
            var episodes = Regex.Matches(title,
                @"\b(?:s\d{1,2})?e(\d{1,3})(?:\s*[-–]\s*e?(\d{1,3}))?|\b(?:episodes?|серии|серия)\s*(\d{1,3})(?:\s*[-–]\s*(\d{1,3}))?", RegexOptions.IgnoreCase);
            if (episodes.Count > 0 && !episodes.Any(match =>
            {
                var firstGroup = match.Groups[1].Success ? 1 : 3;
                var first = int.Parse(match.Groups[firstGroup].Value, CultureInfo.InvariantCulture);
                var last = match.Groups[firstGroup + 1].Success ? int.Parse(match.Groups[firstGroup + 1].Value, CultureInfo.InvariantCulture) : first;
                return requestedEpisode >= first && requestedEpisode <= last;
            })) return false;
        }
        // A year is not a title word, but a conflicting explicit release year is
        // useful evidence against remakes and movies sharing the same name.
        var queryYear = releaseYear?.ToString(CultureInfo.InvariantCulture)
            ?? Regex.Match(query ?? string.Empty, @"\b(?:19|20)\d{2}\b").Value;
        if (queryYear.Length > 0)
        {
            var titleYears = Regex.Matches(title, @"\b(?:19|20)\d{2}\b");
            if (titleYears.Count > 0 && !titleYears.Any(year => year.Value == queryYear
                || releaseYear.HasValue && isSeries && int.Parse(year.Value, CultureInfo.InvariantCulture) >= releaseYear.Value)) return false;
        }
        return true;
    }

    private static long Score(JsonObject item)
    {
        var seeders = Seeders(item);
        var peers = Number(item, "Peers") ?? Number(item, "Leechers") ?? 0;
        // Seeders dominate. Peers break ties without allowing a busy but unseeded swarm to win.
        return Math.Max(-1, seeders) * 1_000_000L + Math.Clamp(peers, 0, 999_999);
    }

    private static int Seeders(JsonObject item) => Number(item, "Seeders") ?? -1;

    private static int? Number(JsonObject item, string name)
    {
        var value = item[name];
        if (value is null) return null;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static string Text(JsonObject item, string name) => item[name]?.ToString() ?? string.Empty;

    private static IEnumerable<string> Tokens(string? text) => Normalize(text)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(x => x.Length >= 3 && !x.All(char.IsDigit));

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length + 2).Append(' ');
        foreach (var character in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }
        return builder.Append(' ').ToString();
    }
}
