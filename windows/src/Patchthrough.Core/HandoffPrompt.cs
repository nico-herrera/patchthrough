namespace Patchthrough.Core;

/// <summary>
/// What the agent is told when a transcript is handed to it.
///
/// These strings exist three times: here, in Handoff.swift (`prompt`, `webPrompt`)
/// and in cli/src/patchthrough.js (`promptFor`, `webPrompt`). All three are the
/// handoff contract in prose, so **keep them in step**. A wording change in one
/// place and not the others means the same meeting produces different instructions
/// depending on which door it went through.
///
/// The two prompts differ because the doors differ. An agent in a repository can
/// read a file, so it is pointed at the staged path. A chat composer cannot, so the
/// document is attached and the prompt says so.
/// </summary>
public static class HandoffPrompt
{
    /// <summary>
    /// For an agent running inside the repository. It names the staged file
    /// relative to the repository root, which is the only path the agent can be
    /// certain of.
    /// </summary>
    public static string ForStagedFile(string relativePath) =>
        $"""
        Read {relativePath}. That file is the transcript of a meeting about this codebase.

        Work out what it asks of this codebase, then tell me before changing anything:

        1. Concrete work items it implies, ordered by what should happen first, with the files or areas involved.
        2. Anything stated as a decision or constraint I shouldn't relitigate.
        3. Anything ambiguous or contradictory, and anything that reads like a transcription error. Ask me rather than guess.
        4. Anything discussed that the code already does, or already contradicts.

        {HandoffDocument.AsrCaveat} Don't edit anything until we've agreed the list.
        """;

    /// <summary>
    /// For a chat composer with the document attached.
    ///
    /// It never names the attached file. One site renames a pasted file to a
    /// generated identifier and drops the extension, so an instruction to read
    /// "handoff.md" would point at a name the model cannot see.
    /// </summary>
    public static string ForAttachedDocument(int durationSeconds) =>
        $"""
        The attached file is the transcript of a meeting I just had ({HandoffDocument.Duration(durationSeconds)}, machine-transcribed on-device). Read it, work out what it asks of me, then give me:

        1. Concrete work items it implies, ordered by what should happen first.
        2. Anything stated as a decision or constraint I shouldn't relitigate.
        3. Anything ambiguous or contradictory, and anything that reads like a transcription error. Ask me rather than guess.

        {HandoffDocument.AsrCaveat}
        """;
}
