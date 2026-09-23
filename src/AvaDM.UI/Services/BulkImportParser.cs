namespace AvaDM.UI.Services;

/// <summary>Turns pasted/loaded text into candidate download links for the bulk-import dialog:
/// one link per line, blank lines and <c>#</c> comment lines skipped, only absolute http(s) URLs
/// kept, and repeats collapsed to their first occurrence.</summary>
public static class BulkImportParser
{
    public static IReadOnlyList<Uri> Parse(string? text)
    {
        var links = new List<Uri>();
        if (string.IsNullOrWhiteSpace(text))
            return links;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                continue;

            if (seen.Add(uri.AbsoluteUri))
                links.Add(uri);
        }

        return links;
    }
}
