using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchthrough.Core;

/// <summary>
/// State that belongs to this machine's copy of the app rather than to the user's
/// settings: where the window was, which handoff destinations get used, which
/// one-time warnings have been dismissed, and the repository a handoff last
/// staged into.
///
/// This is deliberately **not** config.json. That file is shared with the macOS
/// app and the npm CLI, and a window position has no meaning on another machine.
/// It is the counterpart to the macOS app's UserDefaults. It lives in Core rather
/// than beside the Windows code because it holds no platform API at all, and
/// putting it here is what makes its ordering and recovery rules testable.
///
/// It is also deliberately not the registry. The models already live under
/// %LOCALAPPDATA%\patchthrough, so one directory holds everything the app owns
/// and a user can inspect or delete it in one place. A file also replaces
/// atomically, where the registry has no way to write several related values
/// without a crash tearing them apart.
///
/// Local rather than Roaming: every value here describes one machine.
/// </summary>
public sealed class AppState
{
    private readonly string _path;
    private readonly JsonObject _root;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "patchthrough", "state.json");

    private AppState(string path, JsonObject root)
    {
        _path = path;
        _root = root;
    }

    /// <summary>
    /// Read the state. A missing or damaged file is an empty state, never an
    /// error: everything in here is a convenience, and losing a window position
    /// must not stop the app from starting.
    /// </summary>
    public static AppState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed)
            {
                return new AppState(path, parsed);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to an empty state.
        }
        return new AppState(path, new JsonObject());
    }

    /// <summary>The repository a CLI handoff last staged into.</summary>
    public string? HandoffRepository
    {
        get => GetString("handoff.repo");
        set => Set("handoff.repo", value);
    }

    /// <summary>The destination the split button points at.</summary>
    public string? LastDestination
    {
        get => GetString("handoff.last_destination");
        set => Set("handoff.last_destination", value);
    }

    /// <summary>Where the window was, as "x,y,width,height". Null before a first move.</summary>
    public string? WindowFrame
    {
        get => GetString("window.frame");
        set => Set("window.frame", value);
    }

    /// <summary>
    /// How often each destination has been used. This is what orders the "Most
    /// used" section and picks the promoted one-click row.
    /// </summary>
    public int UseCount(string destinationId) =>
        _root["handoff.use_counts"] is JsonObject counts
            && counts[destinationId] is JsonValue value
            && value.TryGetValue(out int count)
            ? count
            : 0;

    /// <summary>Record a use and persist immediately.</summary>
    public void RecordUse(string destinationId)
    {
        var counts = _root["handoff.use_counts"] as JsonObject;
        if (counts is null)
        {
            counts = new JsonObject();
            _root["handoff.use_counts"] = counts;
        }
        counts[destinationId] = UseCount(destinationId) + 1;
        LastDestination = destinationId;
        Save();
    }

    /// <summary>Destination ids, most used first, for the ranked menu.</summary>
    public IReadOnlyList<string> RankedDestinations() =>
        _root["handoff.use_counts"] is not JsonObject counts
            ? []
            : counts
                .Where(pair => pair.Value is JsonValue)
                .OrderByDescending(pair => UseCount(pair.Key))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .ToList();

    /// <summary>
    /// A warning the user chose not to see again. Each one is keyed, so
    /// suppressing the note about one chat site says nothing about another.
    /// </summary>
    public bool IsSuppressed(string warningKey) =>
        _root["suppressed"] is JsonObject suppressed
            && suppressed[warningKey] is JsonValue value
            && value.TryGetValue(out bool flag)
            && flag;

    public void Suppress(string warningKey)
    {
        var suppressed = _root["suppressed"] as JsonObject;
        if (suppressed is null)
        {
            suppressed = new JsonObject();
            _root["suppressed"] = suppressed;
        }
        suppressed[warningKey] = true;
        Save();
    }

    /// <summary>
    /// Write the file. Atomic for the same reason every session file is: a
    /// half-written state file reads as damaged and silently forgets everything.
    /// </summary>
    public void Save()
    {
        var ordered = new JsonObject();
        foreach (var pair in _root.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null) continue;
            ordered[pair.Key] = pair.Value.DeepClone();
        }
        AtomicFile.WriteText(_path, ordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private string? GetString(string key) =>
        _root[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
            ? text
            : null;

    private void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) _root.Remove(key);
        else _root[key] = value;
        Save();
    }
}
