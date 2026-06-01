using System.Text.RegularExpressions;

namespace Yagu.Helpers;

/// <summary>
/// Very lightweight regex-based syntax token classifier for the preview pane.
/// </summary>
public static class SyntaxHighlighter
{
    public enum TokenKind { Plain, Keyword, StringLiteral, Comment, Number }

    public readonly record struct Token(int Start, int Length, TokenKind Kind);

    private static readonly Dictionary<string, HashSet<string>> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = new(StringComparer.Ordinal) { "using","namespace","class","public","private","internal","protected","static","void","var","int","string","bool","true","false","null","new","return","if","else","for","foreach","while","switch","case","default","break","continue","async","await","Task","record","readonly","const","sealed","interface","struct","enum","this","base","throw","try","catch","finally" },
        [".ts"] = new(StringComparer.Ordinal) { "import","export","from","const","let","var","function","return","if","else","for","while","switch","case","default","break","continue","async","await","class","interface","type","extends","implements","new","this","true","false","null","undefined","void","number","string","boolean" },
        [".js"] = new(StringComparer.Ordinal) { "import","export","from","const","let","var","function","return","if","else","for","while","switch","case","default","break","continue","async","await","class","new","this","true","false","null","undefined" },
        [".py"] = new(StringComparer.Ordinal) { "import","from","as","def","class","return","if","elif","else","for","while","try","except","finally","with","yield","lambda","True","False","None","pass","break","continue","async","await","raise","in","is","not","and","or" },
        [".json"] = new(StringComparer.Ordinal) { "true","false","null" },
    };

    private static readonly Regex IdentRe = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex NumberRe = new(@"\b\d+(\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex StringRe = new("(\"[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*\")|('[^'\\\\]*(?:\\\\.[^'\\\\]*)*')", RegexOptions.Compiled);
    private static readonly Regex LineCommentRe = new(@"//.*?$|#.*?$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static IEnumerable<Token> Tokenize(string line, string fileExtension)
    {
        if (string.IsNullOrEmpty(line)) yield break;

        var occupied = new bool[line.Length];

        foreach (Match m in LineCommentRe.Matches(line))
        {
            yield return new Token(m.Index, m.Length, TokenKind.Comment);
            for (int i = 0; i < m.Length; i++) occupied[m.Index + i] = true;
        }
        foreach (Match m in StringRe.Matches(line))
        {
            if (occupied[m.Index]) continue;
            yield return new Token(m.Index, m.Length, TokenKind.StringLiteral);
            for (int i = 0; i < m.Length; i++) occupied[m.Index + i] = true;
        }
        foreach (Match m in NumberRe.Matches(line))
        {
            if (occupied[m.Index]) continue;
            yield return new Token(m.Index, m.Length, TokenKind.Number);
            for (int i = 0; i < m.Length; i++) occupied[m.Index + i] = true;
        }

        if (Keywords.TryGetValue(fileExtension, out var kwSet))
        {
            foreach (Match m in IdentRe.Matches(line))
            {
                if (occupied[m.Index]) continue;
                if (kwSet.Contains(m.Value))
                {
                    yield return new Token(m.Index, m.Length, TokenKind.Keyword);
                }
            }
        }
    }
}
