namespace Flux.Core.Search;

public static class FuzzyMatcher
{
    public static double Score(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        query = Normalize(query);
        candidate = Normalize(candidate);

        if (candidate == query)
        {
            return 1000;
        }

        if (candidate.StartsWith(query, StringComparison.Ordinal))
        {
            return 800 - Math.Min(100, candidate.Length - query.Length);
        }

        var containsAt = candidate.IndexOf(query, StringComparison.Ordinal);
        if (containsAt >= 0)
        {
            return 620 - (containsAt * 8) - Math.Min(80, candidate.Length - query.Length);
        }

        var queryIndex = 0;
        var consecutive = 0;
        var bestConsecutive = 0;
        var gaps = 0;

        for (var index = 0; index < candidate.Length && queryIndex < query.Length; index++)
        {
            if (candidate[index] == query[queryIndex])
            {
                queryIndex++;
                consecutive++;
                bestConsecutive = Math.Max(bestConsecutive, consecutive);
            }
            else if (queryIndex > 0)
            {
                gaps++;
                consecutive = 0;
            }
        }

        if (queryIndex != query.Length)
        {
            return 0;
        }

        return Math.Max(1, 340 + (bestConsecutive * 25) - (gaps * 4) - candidate.Length);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

