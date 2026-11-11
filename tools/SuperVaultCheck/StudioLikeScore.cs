using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Supervertaler.Core.SuperVault;

/// <summary>
/// PROTOTYPE, for measuring only (docs/design/evertm-fuzzy-score.md): a fuzzy score that follows the behaviour
/// described there for Studio's own scorer, written from that description in our own code (no RWS code). If it tracks
/// Studio closely enough, it becomes the proposal for VaultDiff.
///
/// Behaviour:
///  - tokens: words, numbers, punctuation marks, whitespace runs (the tokenizer EverTM already has);
///  - alignment: edit distance over tokens, insert/delete 1, change 1 - similarity:
///      identical 1; same word apart from case or accents 0.95; same stem 0.95; two different punctuation marks (or
///      two different spaces) 0.94; two different numbers 0.85; a word against a number 0; a word, a space and a
///      punctuation mark can't be changed into one another;
///    then a deleted and an inserted token that are (nearly) the same are one move;
///  - charges: an inserted or missing word 1.5 (0.7 for a word of 1-2 characters), punctuation or space 0.1; a changed
///    word 0.2 when nearly the same (case, accents, stem), 0.7 when it looks alike (characters at least 75% the same),
///    else 1.5; a changed punctuation mark or space 0.1; a move 0.8; a changed number: <see cref="NumberChange"/>;
///  - score = 100 - floor(100 * charges / weight of both segments), where a word or number weighs 1 and a punctuation
///    mark or space 0.1; never 100 unless identical;
///  - when the words the two share are mostly stopwords, up to 7 points off (<see cref="Stopwords"/>).
/// </summary>
static class StudioLikeScore
{
    /// <summary>Studio charges nothing for a different number (it replaces it in the target itself). EverTM doesn't, so it charges a changed word.</summary>
    public static double NumberChange = 1.5;
    public static bool UseStopwords = true;
    public static bool DetectMoves = true;
    public static Func<string, string> Stem = s => s;

    enum K { Word, Number, Punct, Space, Tag }

    sealed class T { public K Kind; public string Text, Lower, Folded, Stem; }

    static List<T> Tokens(string s)
    {
        var list = new List<T>();
        foreach (var t in VaultDiff.Tokenize(new[] { new VaultPiece { Text = s } }))
        {
            var k = t.Kind == VaultTokenKind.Space ? K.Space : t.Kind == VaultTokenKind.Punctuation ? K.Punct : t.Kind == VaultTokenKind.Tag ? K.Tag
                  : t.Text.All(char.IsDigit) ? K.Number : K.Word;
            var lower = t.Text.ToLowerInvariant();
            list.Add(new T { Kind = k, Text = t.Text, Lower = lower, Folded = Fold(lower), Stem = k == K.Word ? Stem(lower) : lower });
        }
        return list;
    }

    static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    static double Sim(T a, T b)
    {
        bool aw = a.Kind == K.Word || a.Kind == K.Number, bw = b.Kind == K.Word || b.Kind == K.Number;
        if (a.Kind == K.Space || b.Kind == K.Space) return a.Kind == b.Kind ? (a.Text == b.Text ? 1 : 0.94) : -1;
        if (a.Kind == K.Punct || b.Kind == K.Punct) return a.Kind == b.Kind ? (a.Text == b.Text ? 1 : 0.94) : -1;
        if (a.Kind == K.Tag || b.Kind == K.Tag) return a.Kind == b.Kind ? 0.95 : -1;
        if (a.Kind == K.Number && b.Kind == K.Number) return a.Text == b.Text ? 1 : 0.85;
        if (a.Kind != b.Kind) return 0;   // a word against a number
        if (a.Text == b.Text) return 1;
        if (a.Folded == b.Folded) return 0.95;
        if (a.Stem.Length > 0 && a.Stem == b.Stem) return 0.95;
        return 0;
    }

    /// <summary>How alike two different words look, as a charge band: Studio's character similarity with its thresholds.</summary>
    static double ChangeCharge(T a, T b)
    {
        if (a.Folded == b.Folded || (a.Stem.Length > 0 && a.Stem == b.Stem)) return 0.2;
        if (a.Text.Length + b.Text.Length >= 100) return 1.5;
        double sim = CharSimilarity(a.Text, b.Text);
        return sim >= 0.75 ? 0.7 : 1.5;
    }

    static double CharSimilarity(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var d = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) d[i, 0] = i;
        for (int j = 1; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double s = a[i - 1] == b[j - 1] ? 1 : char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0.95 : 0;
                d[i, j] = Math.Min(d[i - 1, j - 1] + (1 - s), Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
            }
        double dist = d[n, m];
        if (n + m <= 6 && Math.Abs(n - m) <= 1 && dist == 1.0) dist = 0.3;   // one small change in a short word
        double total = n + m;
        return total <= 0 ? 0 : Math.Max(0, (total - 2 * dist) / total);
    }

    enum Op { Same, Change, Ins, Del, Move }

    /// <summary>Points taken off every non-identical score, so that EverTM stays at or below Studio where the two differ slightly.</summary>
    public static int Margin = 0;

    public static int Score(string doc, string mem)
    {
        int raw = RawScore(doc, mem);
        return raw >= 100 ? raw : Math.Max(0, raw - Margin);
    }

    static int RawScore(string doc, string mem)
    {
        if (VaultMarkup.Normalize(doc) == VaultMarkup.Normalize(mem)) return 100;
        var a = Tokens(doc); var b = Tokens(mem);
        int n = a.Count, m = b.Count;
        if (n == 0 || m == 0) return 0;
        var d = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) d[i, 0] = i;
        for (int j = 1; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double s = Sim(a[i - 1], b[j - 1]);
                double ch = s < 0 ? 1e5 : d[i - 1, j - 1] + (1 - s);
                d[i, j] = Math.Min(ch, Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
            }
        var ops = new List<(Op op, int i, int j)>();
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0)
            {
                double s = Sim(a[x - 1], b[y - 1]);
                if (s >= 0 && Math.Abs(d[x, y] - (d[x - 1, y - 1] + 1 - s)) < 1e-9) { ops.Add((s == 1 ? Op.Same : Op.Change, x - 1, y - 1)); x--; y--; continue; }
            }
            if (x > 0 && Math.Abs(d[x, y] - (d[x - 1, y] + 1)) < 1e-9) { ops.Add((Op.Del, x - 1, -1)); x--; continue; }
            ops.Add((Op.Ins, -1, y - 1)); y--;
        }
        ops.Reverse();
        if (DetectMoves)
            for (int k = 0; k < ops.Count; k++)
            {
                if (ops[k].op != Op.Del && ops[k].op != Op.Ins) continue;
                for (int l = k + 1; l < ops.Count; l++)
                {
                    bool pair = ops[k].op == Op.Del && ops[l].op == Op.Ins ? Sim(a[ops[k].i], b[ops[l].j]) >= 0.95
                              : ops[k].op == Op.Ins && ops[l].op == Op.Del && Sim(a[ops[l].i], b[ops[k].j]) >= 0.95;
                    if (!pair) continue;
                    var tok = ops[k].op == Op.Del ? a[ops[k].i] : b[ops[k].j];
                    if (tok.Kind == K.Space) continue;   // a space "moving" means nothing
                    ops[k] = (Op.Move, ops[k].i, ops[k].j);
                    ops.RemoveAt(l);
                    break;
                }
            }

        double weight = a.Concat(b).Sum(t => t.Kind == K.Word || t.Kind == K.Number ? 1.0 : t.Kind == K.Tag ? 0 : 0.1);
        double charge = 0;
        int sameStop = 0, sameOther = 0;
        foreach (var (op, i, j) in ops)
        {
            switch (op)
            {
                case Op.Same:
                    if (a[i].Kind == K.Word) { if (Stopwords.Contains(a[i].Lower)) sameStop++; else sameOther++; }
                    break;
                case Op.Change:
                    var p = a[i]; var q = b[j];
                    if ((p.Kind == K.Space || p.Kind == K.Punct) && (q.Kind == K.Space || q.Kind == K.Punct)) charge += 0.1;
                    else if (p.Kind == K.Number && q.Kind == K.Number) charge += NumberChange;
                    else if (p.Kind == K.Tag) charge += 0;
                    else charge += ChangeCharge(p, q);
                    break;
                case Op.Move: charge += 0.8; break;
                case Op.Del: charge += Gap(a[i]); break;
                case Op.Ins: charge += Gap(b[j]); break;
            }
        }
        if (weight <= 0) weight = 1;
        int off = (int)(charge * 100 / weight);
        if (UseStopwords)
        {
            int wa = a.Count(t => t.Kind == K.Word), wb = b.Count(t => t.Kind == K.Word);
            int sa = a.Count(t => t.Kind == K.Word && Stopwords.Contains(t.Lower)), sb = b.Count(t => t.Kind == K.Word && Stopwords.Contains(t.Lower));
            if (wa > 5 && wb > 5 && sameStop + sameOther > 0)
            {
                float ratio = ((float)sa / wa + (float)sb / wb) / 2f, same = (float)sameStop / (sameStop + sameOther);
                if (ratio < same) off += ratio <= same * 0.65f ? 7 : ratio <= same * 0.8f ? 3 : 0;
            }
            else if (off > 0 && sameStop > 0)
                off += (int)((float)sameStop / (sameStop + sameOther) * (int)(150f / weight));
        }
        if (off > 100) return 0;
        if (off == 0 && charge > 0) off = 1;
        return 100 - off;
    }

    static double Gap(T t) => t.Kind == K.Space || t.Kind == K.Punct ? 0.1 : t.Kind == K.Tag ? 0 : t.Text.Length > 2 ? 1.5 : 0.7;

    /// <summary>A public English stopword list (the Snowball project's, BSD), not Studio's: Studio's lists are its own.</summary>
    public static readonly HashSet<string> Stopwords = new HashSet<string>(
        ("i me my myself we our ours ourselves you your yours yourself yourselves he him his himself she her hers herself it its itself they them their theirs themselves " +
         "what which who whom this that these those am is are was were be been being have has had having do does did doing would should could ought " +
         "a an the and but if or because as until while of at by for with about against between into through during before after above below to from up down in out on off over under " +
         "again further then once here there when where why how all any both each few more most other some such no nor not only own same so than too very").Split(' '), StringComparer.Ordinal);
}
