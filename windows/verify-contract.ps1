$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("patchthrough-contract-" + [guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $recordings = Join-Path $work "Recordings"
    $fixtureOutput = @(dotnet run --project (Join-Path $repo "windows/tools/SessionFixture") -c Release -- $recordings)
    $session = [string]$fixtureOutput[-1]
    foreach ($name in @("meta.json", "transcript.json", "transcript.raw.json", "transcript.md", "handoff.md", "notes.json")) {
        if (-not (Test-Path (Join-Path $session $name))) { throw "fixture did not write $name" }
    }

    # audio_start is what lets a note typed during a meeting be placed on the
    # transcript's clock. Without it a reader falls back to `started`, which is
    # stamped before the capture devices open and therefore lands late.
    $meta = Get-Content -LiteralPath (Join-Path $session "meta.json") -Raw | ConvertFrom-Json
    if (-not $meta.audio_start) { throw "meta.json has no audio_start" }

    # The notes section is prose shared with the macOS app and the npm CLI. The
    # bash verifier compares it byte for byte against the CLI's own rendering;
    # this asserts the two rules that break silently.
    $handoff = Get-Content -LiteralPath (Join-Path $session "handoff.md") -Raw
    if ($handoff -notmatch '(?m)^## Notes$') { throw "handoff.md has no Notes section" }
    if ($handoff -notmatch '(?m)^- \*\*\[0:09\]\*\* ') {
        throw "a note did not render on the transcript clock"
    }
    if ($handoff.IndexOf("## Notes") -gt $handoff.IndexOf("## Transcript")) {
        throw "the notes section must sit above the transcript"
    }

    $list = node (Join-Path $repo "cli/bin/patchthrough.js") transcripts --recordings-dir $recordings
    if (-not ($list -match [regex]::Escape((Split-Path $session -Leaf)))) { throw "CLI did not list Windows fixture" }
    Write-Host "cross-platform session contract passed"
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
