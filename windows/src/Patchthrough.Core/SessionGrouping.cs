using System.Globalization;

namespace Patchthrough.Core;

/// <summary>One date bucket of a session list, and the sessions in it.</summary>
public sealed record SessionGroup(string Title, IReadOnlyList<SessionListing> Sessions);

/// <summary>
/// Groups a session list by calendar day, the way the macOS sidebar does.
///
/// This is here rather than beside the views because the boundaries are where the
/// bugs live: a session recorded at 00:30 belongs to today, one at 23:30
/// yesterday belongs to Yesterday, and "six days ago" has to stop being a
/// weekday name before it starts repeating this week's. Putting it in Core makes
/// every one of those a test rather than something to notice on a Tuesday.
/// </summary>
public static class SessionGrouping
{
    public const string Today = "Today";
    public const string Yesterday = "Yesterday";

    /// <summary>
    /// A session whose folder name does not parse and whose meta.json has no
    /// usable start. It is still recorded audio, so it gets a bucket rather than
    /// being dropped.
    /// </summary>
    public const string Undated = "Undated";

    /// <summary>
    /// Bucket the sessions, keeping the order they arrive in.
    /// </summary>
    /// <param name="now">
    /// The instant "today" is measured from. It is a parameter so the buckets can
    /// be tested, and so a window left open overnight can be re-grouped against
    /// the new day rather than against the day it opened.
    /// </param>
    public static IReadOnlyList<SessionGroup> Group(
        IEnumerable<SessionListing> sessions,
        DateTimeOffset now)
    {
        var buckets = new List<(string Title, List<SessionListing> Sessions)>();
        foreach (var session in sessions)
        {
            var title = TitleFor(session.StartedAt, now);
            var existing = buckets.FindIndex(bucket => string.Equals(bucket.Title, title, StringComparison.Ordinal));
            if (existing >= 0) buckets[existing].Sessions.Add(session);
            else buckets.Add((title, [session]));
        }
        return buckets.Select(bucket => new SessionGroup(bucket.Title, bucket.Sessions)).ToList();
    }

    /// <summary>
    /// The bucket one session belongs in. Today, then Yesterday, then a weekday
    /// name inside the last week, then an absolute date.
    /// </summary>
    public static string TitleFor(DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null) return Undated;

        // Compared as local calendar days, not as elapsed hours. A meeting at
        // 23:30 and one at 00:30 are 60 minutes apart and belong to different
        // days, which is what a person reading the list expects.
        var day = startedAt.Value.LocalDateTime.Date;
        var today = now.LocalDateTime.Date;

        if (day == today) return Today;
        if (day == today.AddDays(-1)) return Yesterday;

        var age = (today - day).Days;
        // Inside the last week a weekday name is the most readable label. At
        // seven days it would start repeating the current day's name, so the
        // absolute date takes over before that can happen.
        if (age is > 0 and < 7)
        {
            return day.ToString("dddd", CultureInfo.CurrentCulture);
        }
        // A session dated in the future, which a clock change can produce, falls
        // through to the absolute date rather than claiming to be a weekday.
        return day.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
