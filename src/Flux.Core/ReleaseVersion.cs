namespace Flux.Core;

public static class ReleaseVersion
{
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var suffixIndex = value.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            value = value[..suffixIndex];
        }

        if (!Version.TryParse(value, out var parsed) || parsed.Major < 0 || parsed.Minor < 0)
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    public static int Compare(Version left, Version right) => Normalize(left).CompareTo(Normalize(right));

    public static Version Normalize(Version version) => new(
        version.Major,
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));
}
