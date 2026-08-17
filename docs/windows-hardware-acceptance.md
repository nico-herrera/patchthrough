# Windows hardware acceptance

The integration PR must stay draft until this checklist is run on a physical Windows
machine. Record the Windows build, hardware/audio devices, observed artifact paths,
and logs beside each result. A checked box without evidence is not acceptance.

- [ ] Microphone and WASAPI loopback files both contain playable audio.
- [ ] Audio played only in the middle of a recording remains aligned with the mic;
      silence padding covers the beginning and end.
- [ ] AAC is produced when Media Foundation supports it.
- [ ] Windows N falls back to WAV and `meta.json` names the actual files.
- [ ] Pinned Parakeet and Whisper models resume, verify, load, and transcribe the smoke
      and representative meeting samples.
- [ ] Vulkan/available acceleration is selected when healthy and safely falls back to
      CPU (including no-AVX where applicable).
- [ ] Missing models, missing devices, codec absence, and a mid-session device change
      leave a recoverable session and never a false completion marker.
- [ ] `patchthrough transcripts` lists the Windows-authored session.
- [ ] `patchthrough hand <agent>` consumes it without platform reconstruction.
- [ ] The signed installer reports the expected publisher, installs without
      administrator access, makes `Patchthrough` available in a new terminal, and
      cleanly removes the executable and its PATH entry on uninstall. The
      expected publisher is `SignPath Foundation`, because the certificate is
      issued to the Foundation rather than to the project. A build signed with
      the `test-signing` policy reports `Patchthrough (Test)` and Windows does
      not trust it, so it cannot satisfy this item.
- [ ] The portable ZIP runs on a clean x64-compatible Windows 10 1809+ or Windows 11 machine
      without a separately installed .NET runtime.
- [ ] Both executables in the shared directory start: `Patchthrough.exe` prints help from a
      terminal, and `PatchthroughApp.exe` puts an icon in the tray. They share one
      self-contained runtime, and each carries its own runtimeconfig.json, so a missing
      assembly would show up as one of the two failing to launch while the other works.
- [ ] `Patchthrough.exe rec` still stops on Ctrl+C and on Enter from a terminal, and the
      shell waits for it. This is why the console tool stayed a console application.
- [ ] The Start menu entry launches the app, and a second launch raises the existing window
      rather than starting a second copy.
- [ ] Starting a recording in the app makes `Patchthrough.exe rec` in a terminal refuse with
      a message, and the reverse. Two recorders on one microphone is the failure this
      prevents, and Windows permits it silently.
- [ ] Uninstalling removes the Start menu entry and the sign-in registry entry. A leftover
      sign-in entry launches a deleted executable and Windows reports it to the user.
- [ ] The window opens by itself when a recording starts, and the note field takes a note
      on Return. This is the one pane the CI screenshot harness cannot capture, because it
      exists only while real capture devices are running.
- [ ] A note typed immediately after a spoken phrase resolves onto that phrase's line in
      `handoff.md`. This is the end-to-end check for `audio_start`: subtracting `started`
      instead lands over a second late, which is a different sentence in a dense
      conversation. Record the spoken timestamp and the note's rendered timestamp.
- [ ] `patchthrough hand <agent>` from the npm CLI and the app's own patch-through button
      produce the same staged file and the same prompt for one session.

Suggested evidence commands:

```powershell
dotnet test windows/Patchthrough.sln -c Release
dotnet run --project windows/src/Patchthrough.Windows -c Release -- doctor
dotnet run --project windows/src/Patchthrough.Windows -c Release -- rec --out $env:TEMP\patchthrough-acceptance
node cli/bin/patchthrough.js transcripts --out $env:TEMP\patchthrough-acceptance
windows\packaging\verify-release.ps1 -ExpectedVersion <version>
Get-AuthenticodeSignature dist\Patchthrough-windows-x64-setup.exe | Format-List
```
