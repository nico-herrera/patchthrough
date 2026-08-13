using System.Text;
using System.Text.Json.Nodes;

namespace Patchthrough.Core;

/// <summary>
/// A chat site that accepts a pasted transcript.
/// </summary>
/// <param name="PrefillsPrompt">
/// The site reads a prompt from the URL. When it does not, opening a plain chat is
/// better than sending a query it will show as literal text.
/// </param>
/// <param name="UploadsToCloud">
/// The site copies the attachment off this machine. Patchthrough transcribes
/// on-device and uploads nothing on its own, so a door that does has to say so
/// before it is used.
/// </param>
public sealed record ChatSite(
    string Id,
    string Label,
    string NewChatUrl,
    bool PrefillsPrompt,
    bool UploadsToCloud,
    bool IsCustom = false)
{
    public string DestinationId => $"gui:{Id}";
}

/// <summary>
/// The chat sites a transcript can be handed to: the shipped ones, plus whatever
/// the user added to `custom_destinations` in the config.
///
/// The table and the validation mirror WEB_TARGETS and `webTargets` in
/// cli/src/patchthrough.js. Keep them in step, and keep the validation strict for
/// the reason the CLI states: the URL reaches a shell-execute call, which hands any
/// scheme to whichever application claims it.
/// </summary>
public static class DestinationCatalog
{
    public static readonly IReadOnlyList<ChatSite> Shipped =
    [
        new("web-claude", "Claude (web)", "https://claude.ai/new",
            PrefillsPrompt: true, UploadsToCloud: false),
        new("web-chatgpt", "ChatGPT (web)", "https://chatgpt.com/",
            PrefillsPrompt: true, UploadsToCloud: false),
        // The only way to attach a file to Microsoft 365 Copilot. It keeps a copy,
        // which is why it is the one shipped site that declares an upload.
        new("web-m365", "Microsoft 365 Copilot (web)", "https://m365.cloud.microsoft/chat/",
            PrefillsPrompt: false, UploadsToCloud: true),
    ];

    /// <summary>
    /// The shipped sites plus the user's own. A custom entry that fails validation
    /// is dropped with a warning rather than silently ignored: a destination that
    /// quietly never appears is harder to diagnose than one that explains itself.
    /// </summary>
    public static IReadOnlyList<ChatSite> Resolve(Config config, TextWriter? warnings = null)
    {
        var sites = new List<ChatSite>(Shipped);
        foreach (var custom in config.CustomDestinations(warnings))
        {
            // A custom entry with a shipped id replaces it, which is how a user
            // points an existing door somewhere else.
            sites.RemoveAll(site => string.Equals(site.Id, custom.Id, StringComparison.Ordinal));
            sites.Add(custom);
        }
        return sites;
    }

    /// <summary>
    /// Percent-encode down to letters and digits.
    ///
    /// Deliberately stricter than the usual encoders, which leave `'()*~!.-_` raw.
    /// The sites read the query with URLSearchParams, and this has to produce the
    /// same bytes the macOS app and the npm CLI send, so the same prompt arrives
    /// whichever door it goes through. `pctEncoded` in the CLI is the reference.
    /// </summary>
    public static string PercentEncode(string value)
    {
        var builder = new StringBuilder(value.Length * 3);
        // One buffer for the whole string. A stackalloc inside the loop would grow
        // the frame once per character.
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && char.IsAsciiLetterOrDigit((char)rune.Value))
            {
                builder.Append((char)rune.Value);
                continue;
            }
            var written = rune.EncodeToUtf8(utf8);
            for (var index = 0; index < written; index++)
            {
                builder.Append('%').Append(utf8[index].ToString("X2"));
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// The URL to open for a site, with the prompt attached when the site reads one.
    ///
    /// Built through a UriBuilder rather than by concatenation: a configured URL can
    /// already carry a query or a fragment, and appending `?q=` by hand would put
    /// the query inside the fragment, where the page never reads it.
    /// </summary>
    public static string UrlFor(ChatSite site, string? prompt)
    {
        if (prompt is null || !site.PrefillsPrompt) return site.NewChatUrl;

        var builder = new UriBuilder(site.NewChatUrl);
        var query = builder.Query.TrimStart('?');
        var prefill = $"q={PercentEncode(prompt)}";
        builder.Query = query.Length == 0 ? prefill : $"{query}&{prefill}";
        return builder.Uri.AbsoluteUri;
    }
}
