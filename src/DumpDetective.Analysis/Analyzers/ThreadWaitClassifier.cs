using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

internal readonly struct WaitPattern
{
    public readonly string Category;
    public readonly string Token;
    public readonly string Reason;

    public WaitPattern(string category, string token, string reason)
    {
        Category = category;
        Token = token;
        Reason = reason;
    }
}

internal readonly struct WaitClassification
{
    public readonly string Category;
    public readonly string Reason;

    public WaitClassification(string category, string reason)
    {
        Category = category;
        Reason = reason;
    }
}

// Matching engine shared by callers that classify a thread's wait state from a caller-supplied
// WaitPattern table. Deliberately not a single canonical table: HangAnalyzer's IOWait rule is a
// compound AND-of-two-substrings check that this single-token model can't represent, so it keeps
// its own private classification logic and only shares stack-frame acquisition via the dispatcher.
internal static class ThreadWaitClassifier
{
    public static WaitClassification? Classify(IReadOnlyList<ClrStackFrame> frames, IReadOnlyList<WaitPattern> patterns)
    {
        foreach (var frame in frames)
        {
            WaitClassification? match = ClassifySignature(GetFrameSignature(frame), patterns);
            if (match != null)
                return match;
        }

        return null;
    }

    public static WaitClassification? ClassifySignature(string signature, IReadOnlyList<WaitPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (signature.Contains(pattern.Token, StringComparison.OrdinalIgnoreCase))
                return new WaitClassification(pattern.Category, pattern.Reason);
        }

        return null;
    }

    public static string GetFrameSignature(ClrStackFrame frame) =>
        frame.Method?.Signature ?? frame.FrameName ?? string.Empty;
}
