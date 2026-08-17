using System.Text.Json.Nodes;
using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// One config file is shared by the Windows app, the macOS app, and the npm CLI,
/// so a write that loses a key breaks a setting on another platform. These tests
/// assert the two rules that protect that: unrelated keys survive every save,
/// and only deliberate overrides are stored.
/// </summary>
public sealed class ConfigUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-config-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _path;

    public ConfigUpdateTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "config.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JsonObject Written() => JsonNode.Parse(File.ReadAllText(_path))!.AsObject();

    [Fact]
    public void AFirstSaveCreatesTheFileAndItsDirectory()
    {
        // The config file only exists once a setting has been saved, so the
        // first save has to make the ~/.config/patchthrough directory too.
        var path = Path.Combine(_root, "nested", "config.json");

        Config.Update(new Dictionary<string, object?> { ["recordings_dir"] = "D:\\Meetings" }, path);

        Assert.Equal("D:\\Meetings", (string?)JsonNode.Parse(File.ReadAllText(path))!["recordings_dir"]);
    }

    [Fact]
    public void ADottedKeyAddressesANestedObject()
    {
        Config.Update(new Dictionary<string, object?> { ["transcription.enabled"] = false }, _path);

        Assert.False((bool?)Written()["transcription"]!["enabled"]);
    }

    [Fact]
    public void AKeyWithTwoDotsIsAFlatKeySpelledLiterally()
    {
        // Only a single dot nests, matching Config.swift. A deeper key would
        // otherwise silently create a shape neither reader looks in.
        Config.Update(new Dictionary<string, object?> { ["a.b.c"] = 1 }, _path);

        Assert.Equal(1, (int?)Written()["a.b.c"]);
        Assert.Null(Written()["a"]);
    }

    [Fact]
    public void SavingOneSettingKeepsEveryOtherKeyInTheFile()
    {
        File.WriteAllText(_path, """
        {
          "on_stop": "/Users/me/bin/after-meeting",
          "terminal": "iTerm",
          "transcription": { "engine": "whisper", "project_dir": "~/code/app" }
        }
        """);

        Config.Update(new Dictionary<string, object?> { ["transcription.enabled"] = false }, _path);

        var written = Written();
        // A hook the user set by hand, a macOS-only terminal choice, and a
        // sibling of the key that changed all have to survive.
        Assert.Equal("/Users/me/bin/after-meeting", (string?)written["on_stop"]);
        Assert.Equal("iTerm", (string?)written["terminal"]);
        Assert.Equal("whisper", (string?)written["transcription"]!["engine"]);
        Assert.Equal("~/code/app", (string?)written["transcription"]!["project_dir"]);
        Assert.False((bool?)written["transcription"]!["enabled"]);
    }

    [Fact]
    public void NullRemovesAKeySoTheFileHoldsOnlyDeliberateOverrides()
    {
        File.WriteAllText(_path, """{ "recordings_dir": "D:\\Meetings", "terminal": "wt" }""");

        Config.Update(new Dictionary<string, object?> { ["recordings_dir"] = null }, _path);

        // Back to the default, which is an absent key rather than a stored copy
        // of the default value.
        Assert.Null(Written()["recordings_dir"]);
        Assert.Equal("wt", (string?)Written()["terminal"]);
    }

    [Fact]
    public void NullRemovesANestedKey()
    {
        File.WriteAllText(_path, """{ "transcription": { "enabled": false, "engine": "whisper" } }""");

        Config.Update(new Dictionary<string, object?> { ["transcription.enabled"] = null }, _path);

        Assert.Null(Written()["transcription"]!["enabled"]);
        Assert.Equal("whisper", (string?)Written()["transcription"]!["engine"]);
    }

    [Fact]
    public void AnObjectEmptiedByRemovalsGoesToo()
    {
        File.WriteAllText(_path, """{ "transcription": { "enabled": false } }""");

        Config.Update(new Dictionary<string, object?> { ["transcription.enabled"] = null }, _path);

        // Leaving "transcription": {} behind would be a stored default.
        Assert.Null(Written()["transcription"]);
    }

    [Fact]
    public void RemovingAKeyThatWasNeverThereIsNotAnError()
    {
        // Every save sends the full set of settings, and most of them are at
        // their default, so this is the common case rather than an edge one.
        Config.Update(new Dictionary<string, object?>
        {
            ["recordings_dir"] = null,
            ["transcription.enabled"] = null,
        }, _path);

        Assert.Empty(Written());
    }

    [Fact]
    public void AMalformedFileIsReplacedRatherThanFailingTheSave()
    {
        File.WriteAllText(_path, "{ not json");

        Config.Update(new Dictionary<string, object?> { ["terminal"] = "wt" }, _path);

        // Config.Load already ignores a malformed file, so its keys were not in
        // effect. Refusing to save would leave the user unable to fix it.
        Assert.Equal("wt", (string?)Written()["terminal"]);
    }

    [Fact]
    public void EveryValueTypeTheSettingsSurfaceNeedsRoundTrips()
    {
        Config.Update(new Dictionary<string, object?>
        {
            // A tilde path, because this test runs on macOS too and a drive
            // letter resolves against the working directory there.
            ["recordings_dir"] = "~/Meetings",
            ["transcription.enabled"] = true,
            ["transcription.quality_mode"] = "max_accuracy",
            ["custom_destinations"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "internal",
                    ["label"] = "Internal chat",
                    ["url"] = "https://chat.example.com/new",
                    ["uploads_to_cloud"] = true,
                }),
        }, _path);

        var reloaded = Config.Load(_path);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Meetings"),
            reloaded.ResolveRecordingsRoot());
        Assert.True(reloaded.TranscriptionEnabled);
        Assert.Equal(QualityMode.MaxAccuracy, reloaded.TranscriptionQualityMode);
        Assert.Equal("internal", (string?)Written()["custom_destinations"]![0]!["id"]);
    }

    [Fact]
    public void AValueTypeTheConfigCannotHoldIsRefusedRatherThanWrittenWrong()
    {
        Assert.Throws<ArgumentException>(() =>
            Config.Update(new Dictionary<string, object?> { ["terminal"] = new object() }, _path));
    }

    [Fact]
    public void KeysAreSortedAtEveryLevelSoTwoIdenticalSavesAreIdentical()
    {
        Config.Update(new Dictionary<string, object?>
        {
            ["terminal"] = "wt",
            ["transcription.engine"] = "parakeet",
            ["recordings_dir"] = "D:\\Meetings",
            ["transcription.enabled"] = true,
        }, _path);
        var first = File.ReadAllText(_path);

        Config.Update(new Dictionary<string, object?> { ["terminal"] = "wt" }, _path);

        // A stable order keeps a hand-edited config diffable, and keeps a save
        // that changed nothing from showing up as a change.
        Assert.Equal(first, File.ReadAllText(_path));
        var keys = Written().Select(pair => pair.Key);
        Assert.Equal(["recordings_dir", "terminal", "transcription"], keys);
        Assert.Equal(["enabled", "engine"], Written()["transcription"]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public void TheSaveIsAtomicAndLeavesNoTemporaryFile()
    {
        Config.Update(new Dictionary<string, object?> { ["terminal"] = "wt" }, _path);

        // A half-written config reads as malformed on the next launch, which
        // would silently drop every setting the user has.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void AJsonNullAlreadyInTheFileIsDropped()
    {
        File.WriteAllText(_path, """{ "terminal": null, "recordings_dir": "D:\\Meetings" }""");

        Config.Update(new Dictionary<string, object?> { ["transcription.enabled"] = false }, _path);

        Assert.False(Written().ContainsKey("terminal"));
    }
}
