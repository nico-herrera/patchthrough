using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchthrough.Core;

/// <summary>
/// The user config, mirroring Config.swift. The path is deliberately the same
/// on every platform: the npm CLI reads
/// `~/.config/patchthrough/config.json` from the home directory with no
/// platform branch, so a second location would split the state of one machine.
/// </summary>
public sealed class Config
{
    private readonly JsonObject _root;

    public static string DefaultPath => Path.Combine(Home, ".config", "patchthrough", "config.json");

    public static string DefaultRecordingsRoot => Path.Combine(Home, "Recordings");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private Config(JsonObject root) => _root = root;

    /// <summary>
    /// Read the config. A malformed file is reported and then ignored, the way
    /// the macOS app reports it. A recording that lands somewhere unexpected is
    /// worse than a warning.
    /// </summary>
    public static Config Load(string? path = null, TextWriter? warnings = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new Config(new JsonObject());
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is JsonObject obj) return new Config(obj);
            throw new JsonException("the config file is not a JSON object");
        }
        catch (Exception)
        {
            (warnings ?? Console.Error).WriteLine($"warning: {path} is not valid JSON. Ignoring config");
            return new Config(new JsonObject());
        }
    }

    /// <summary>
    /// Resolution order matches the macOS app: the command line wins, then the
    /// config file, then the default.
    /// </summary>
    public string ResolveRecordingsRoot(string? commandLineOverride = null)
    {
        if (!string.IsNullOrEmpty(commandLineOverride)) return ExpandHome(commandLineOverride);
        var configured = String("recordings_dir");
        return configured is null ? DefaultRecordingsRoot : ExpandHome(configured);
    }

    public bool TranscriptionEnabled => NestedBool("transcription", "enabled") ?? true;

    public string TranscriptionEngine => NestedString("transcription", "engine") ?? "auto";

    public QualityMode TranscriptionQualityMode =>
        string.Equals(NestedString("transcription", "quality_mode"), "max_accuracy", StringComparison.Ordinal)
            ? QualityMode.MaxAccuracy
            : QualityMode.Standard;

    public string? TranscriptionProjectDirectory
    {
        get
        {
            var configured = NestedString("transcription", "project_dir");
            return configured is null ? null : ExpandHome(configured);
        }
    }

    public bool DedupMicEcho => NestedBool("transcription", "dedup_mic_echo") ?? true;

    /// <summary>
    /// Bring the window up when a recording starts. On by default, because the note
    /// field is in the window and notes are typed during the meeting, not after it.
    /// </summary>
    public bool NotesOpenWindowOnRecord => NestedBool("notes", "open_window_on_record") ?? true;

    public bool MicVoiceProcessing => Bool("mic_voice_processing") ?? false;

    public string? OnStop => String("on_stop");

    /// <summary>
    /// Chat destinations the user added to the config.
    ///
    /// The validation mirrors `webTargets` in cli/src/patchthrough.js and
    /// `customDestinations` in Config.swift, and it is strict on purpose. The id
    /// becomes part of a destination key and a menu item, and the URL is handed to
    /// the shell, which passes any scheme to whichever application claims it. An
    /// entry that fails is reported and dropped rather than repaired.
    /// </summary>
    public IReadOnlyList<ChatSite> CustomDestinations(TextWriter? warnings = null)
    {
        if (_root["custom_destinations"] is not JsonArray rows) return [];
        var report = warnings ?? Console.Error;
        var sites = new List<ChatSite>();

        foreach (var row in rows)
        {
            if (row is not JsonObject entry) continue;

            var id = entry["id"] is JsonValue idValue && idValue.TryGetValue(out string? rawId) ? rawId : null;
            if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                report.WriteLine("warning: ignoring a custom_destinations entry: bad or missing \"id\"");
                continue;
            }

            var url = entry["url"] is JsonValue urlValue && urlValue.TryGetValue(out string? rawUrl) ? rawUrl : null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                report.WriteLine(
                    $"warning: ignoring custom destination \"{id}\": \"url\" must start with http:// or https://");
                continue;
            }

            var label = entry["label"] is JsonValue labelValue
                && labelValue.TryGetValue(out string? rawLabel) && !string.IsNullOrEmpty(rawLabel)
                ? rawLabel
                : id;
            // Both flags default the way the CLI defaults them: a site is assumed
            // to read a prompt, and assumed not to keep a copy. The second default
            // is the safe one to state explicitly rather than to guess.
            var prefills = entry["prefills_prompt"] is not JsonValue prefillValue
                || !prefillValue.TryGetValue(out bool prefill)
                || prefill;
            var uploads = entry["uploads_to_cloud"] is JsonValue uploadValue
                && uploadValue.TryGetValue(out bool upload)
                && upload;

            sites.Add(new ChatSite(id, label, parsed.AbsoluteUri, prefills, uploads, IsCustom: true));
        }
        return sites;
    }

    /// <summary>Where a repository picker starts when patching a transcript through.</summary>
    public string? ProjectsDirectory
    {
        get
        {
            var configured = String("projects_dir");
            return configured is null ? null : ExpandHome(configured);
        }
    }

    /// <summary>
    /// The terminal a CLI agent is started in. The shell profile the agent sees
    /// comes from this choice rather than from the system default.
    /// </summary>
    public string? Terminal => String("terminal");

    /// <summary>
    /// Merge values into the config file, creating it if needed. This mirrors
    /// `Config.update` in Sources/patchthrough/Config.swift, because both
    /// platforms write the same file.
    ///
    /// A key mapped to null is removed, so the file only ever holds deliberate
    /// overrides instead of a dump of every default. A key with exactly one dot
    /// addresses a nested object ("transcription.enabled"); any other key,
    /// including one with two dots, is a flat key spelled literally.
    ///
    /// Values may be bool, string, int, long, double, or a JsonNode for
    /// anything with structure, such as `custom_destinations`.
    ///
    /// Callers that save settings must never pass `on_stop`. The macOS app
    /// leaves that key alone for the same reason: it has no control in the UI,
    /// so writing it would delete a hook the user set by hand.
    /// </summary>
    public static void Update(IReadOnlyDictionary<string, object?> changes, string? path = null)
    {
        path ??= DefaultPath;

        // Re-read rather than reuse a loaded instance: the file is shared with
        // the npm CLI and the macOS app, so an in-memory copy can be stale.
        // A malformed file is treated as empty, exactly as Load reports it.
        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed
                ? parsed
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        foreach (var (key, value) in changes)
        {
            var parts = key.Split('.');
            if (parts.Length == 2)
            {
                var nested = root[parts[0]] is JsonObject existing
                    ? existing.Deserialize<JsonObject>() ?? new JsonObject()
                    : new JsonObject();
                if (value is null) nested.Remove(parts[1]);
                else nested[parts[1]] = ToNode(value, key);
                // An object emptied by removals goes too. Leaving
                // `"transcription": {}` behind would be a stored default.
                if (nested.Count == 0) root.Remove(parts[0]);
                else root[parts[0]] = nested;
            }
            else if (value is null) root.Remove(key);
            else root[key] = ToNode(value, key);
        }

        AtomicFile.WriteText(path, SortedJson(root));
    }

    /// <summary>
    /// Keys sorted at every level, which is what Swift's `.sortedKeys` writes.
    /// System.Text.Json emits insertion order and cannot sort, so the tree is
    /// rebuilt. A stable order keeps a hand-edited config diffable and keeps
    /// two saves of the same values byte-identical.
    /// </summary>
    private static string SortedJson(JsonObject root) =>
        SortObject(root).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonObject SortObject(JsonObject source)
    {
        var ordered = new JsonObject();
        foreach (var pair in source.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // A JSON null already in the file is dropped, matching the
            // `compactMapValues` pass on macOS.
            if (pair.Value is null) continue;
            ordered[pair.Key] = Sorted(pair.Value);
        }
        return ordered;
    }

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                return SortObject(obj);
            case JsonArray array:
                // Array order is data, so only the objects inside are sorted.
                var ordered = new JsonArray();
                foreach (var item in array) ordered.Add(Sorted(item));
                return ordered;
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode ToNode(object value, string key) => value switch
    {
        JsonNode node => node.DeepClone(),
        bool flag => JsonValue.Create(flag),
        string text => JsonValue.Create(text),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        _ => throw new ArgumentException(
            $"cannot write '{key}': {value.GetType().Name} is not a config value type", nameof(value)),
    };

    /// <summary>
    /// `~` and `~/` are accepted on Windows too, because the same config file
    /// can come from a Mac.
    /// </summary>
    public static string ExpandHome(string value)
    {
        if (value == "~") return Home;
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Home, value[2..]);
        }
        return Path.GetFullPath(value);
    }

    private string? String(string key) =>
        _root[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
            ? text
            : null;

    private bool? Bool(string key) =>
        _root[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private string? NestedString(string parent, string key) =>
        _root[parent] is JsonObject obj && obj[key] is JsonValue value
            && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
            ? text
            : null;

    private bool? NestedBool(string parent, string key) =>
        _root[parent] is JsonObject obj && obj[key] is JsonValue value
            && value.TryGetValue(out bool flag)
            ? flag
            : null;
}
