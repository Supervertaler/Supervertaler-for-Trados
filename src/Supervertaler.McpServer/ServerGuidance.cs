namespace Supervertaler.McpServer;

/// <summary>
/// The MCP "instructions" this server sends when a client connects: translation
/// craft every AI helping with a translation should keep in mind, whatever the
/// tool it calls. Clients add them to the model's context (Claude Desktop,
/// ChatGPT/Codex), so they reach the AI even before it reads a tool description.
///
/// <para>Kept short and general - any language pair, any kind of document - and
/// specific only where the hosts differ: Trados has merge_segments, memoQ gives a
/// plugin no way to join rows, Workbench's server is read-only.</para>
/// </summary>
public static class ServerGuidance
{
    public static string For(string host)
    {
        var how = host switch
        {
            "memoq" => "memoQ gives this assistant no way to join or split rows. Propose the merge to the user, ask them to " +
                       "join the rows in memoQ (select both, Ctrl+J), then read the segments again before translating.",
            "workbench" => "This connection cannot change segments. Propose the merge to the user, who can join the " +
                           "segments in Supervertaler Workbench.",
            _ => "Use merge_segments (and split_segment to undo it or to split a segment Studio cut wrongly), after " +
                 "the user agrees.",
        };

        return
            "Segment boundaries: a CAT tool cuts segments by the source language's sentence rules, not by what reads " +
            "well in the target language. When neighbouring short source sentences in one paragraph would naturally be " +
            "one sentence in the target language - a question and its answer (\"X? Then Y.\" becomes \"If X, Y.\"), " +
            "a fragment and its completion, a sentence cut at a colon - merging the segments usually gives the better " +
            "translation. Do not merge across paragraphs, headings, list items or table cells, nor where one-to-one " +
            "segment alignment matters (TM-sensitive or legal and patent work, subtitles, strings with length limits). " +
            "Merging changes the document's structure and the TM, so propose it and let the user decide unless they " +
            "have said to merge wherever it helps. Never put a joined translation in one segment and leave the other " +
            "empty instead. " + how;
    }
}
