# First-document crash fixture

`Acme PROJ-001 comment over segments.txt.sdlxliff` makes Trados Studio report two
"SDL XLIFF validation error" messages when it opens the file. The second paragraph's
two segments sit inside a comment marker in `<seg-source>`, which is how Studio itself
stores a Word comment that spans whole sentences. Studio's schema check (the key
`U_tu_segsrc_mid` in its embedded XLIFF 1.2 schema) only looks at segment markers
directly under `<seg-source>`, so it reports one error per nested segment. The file
opens normally apart from that.

Up to 20.204, a file like this crashed Studio when it was the first document opened
after Studio started: Studio showed a NullReferenceException
(`TabbedViewControl.UpdateViewPartProperties`) and the document did not open. The
cause and the fix are described in `src/Supervertaler.Trados/Core/StudioEditor.cs`.

## Test

1. Start Trados Studio 2026 or 2024 with no document open, and wait until it is
   fully up, so Supervertaler's start-up has run (its diagnostic log shows
   `[BridgeLoader] ForcePane (studio-startup)`).
2. Open this file before any other document, as a single document.
3. Expected: the file opens in the Editor, and the Messages window lists the two
   validation errors. With 20.204 and earlier: the error, and no document.

The file has been checked against Studio's three embedded schemas (the same check
Studio runs), not yet opened in Studio itself.
