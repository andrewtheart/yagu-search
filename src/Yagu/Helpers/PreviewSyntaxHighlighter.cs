using System;
using System.Collections.Generic;

namespace Yagu.Helpers;

/// <summary>
/// Token classes emitted by <see cref="PreviewSyntaxHighlighter"/>. Anything not
/// covered by one of these is left uncolored (the preview pane's normal
/// match-line / context-line brush applies).
/// </summary>
public enum PreviewTokenKind
{
    Keyword,
    StringLiteral,
    Comment,
    Number,
    /// <summary>C/C++ preprocessor directives, shell-style directives, LaTeX commands.</summary>
    Preprocessor,
    /// <summary>Markup element names, INI/TOML section headers, JSON property names, Markdown headings.</summary>
    Tag,
    /// <summary>Markup attribute names, INI/TOML/CSS property keys, shell variables.</summary>
    Attribute,
}

/// <summary>
/// Single-line, allocation-light syntax classifier used by the preview pane.
/// <para>
/// The preview renders one physical source line (or one slice of a very long line)
/// at a time and has no cross-line state, so this classifier is deliberately
/// line-local: a block comment or a multi-line string is only recognised on the
/// lines where its delimiters appear. That trade keeps preview rendering O(n) in
/// the rendered text and independent of file size, which matters because a single
/// preview build can emit thousands of paragraphs.
/// </para>
/// <para>
/// Languages are selected with <see cref="EditorSyntaxHighlightingResolver"/> so the
/// preview and the built-in editor agree on which file types are colored.
/// </para>
/// </summary>
public static class PreviewSyntaxHighlighter
{
    /// <summary>A classified, non-overlapping run of characters within the scanned text.</summary>
    public readonly record struct Token(int Start, int Length, PreviewTokenKind Kind)
    {
        public int End => Start + Length;
    }

    /// <summary>Upper bound on tokens produced for one line; pathological lines stop being colored past this.</summary>
    public const int DefaultMaxTokens = 512;

    /// <summary>True when <paramref name="language"/> has a classifier (all resolver languages do today).</summary>
    public static bool IsSupported(EditorSyntaxLanguage language) => true;

    /// <summary>
    /// Classifies <paramref name="text"/> (a single rendered preview line) and returns
    /// ordered, non-overlapping tokens. Regions with no token keep the caller's base color.
    /// </summary>
    public static List<Token> Tokenize(string text, EditorSyntaxLanguage language, int maxTokens = DefaultMaxTokens)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            return tokens;

        switch (language)
        {
            case EditorSyntaxLanguage.Html:
            case EditorSyntaxLanguage.XML:
                ScanMarkup(text, tokens, maxTokens);
                break;
            case EditorSyntaxLanguage.Markdown:
                ScanMarkdown(text, tokens, maxTokens);
                break;
            default:
                ScanCode(text, RulesFor(language), tokens, maxTokens);
                break;
        }

        return tokens;
    }

    // ─────────────────────────────── language rules ───────────────────────────────

    private sealed class Rules
    {
        public string[] LineComments = Array.Empty<string>();
        /// <summary>Whole words that start a comment for the rest of the line (batch <c>rem</c>).</summary>
        public string[] WordLineComments = Array.Empty<string>();
        public (string Start, string End)? BlockComment;
        public bool DoubleQuoteStrings = true;
        public bool SingleQuoteStrings = true;
        public bool BacktickStrings;
        public bool BackslashEscapes = true;
        public bool Numbers = true;
        public HashSet<string>? Keywords;
        /// <summary>C/C++-style <c>#directive</c> lines (plus <c>&lt;header&gt;</c> include targets).</summary>
        public bool Preprocessor;
        /// <summary><c>[section]</c> headers at the start of a line (INI/TOML).</summary>
        public bool SectionHeaders;
        /// <summary>Colors <c>key</c> in <c>key = value</c> / <c>key:</c> as an attribute.</summary>
        public bool KeyValueKeys;
        /// <summary>Colors a quoted string followed by <c>:</c> as a property name (JSON).</summary>
        public bool JsonPropertyNames;
        /// <summary>Colors <c>$name</c> / <c>%NAME%</c> variables as attributes.</summary>
        public bool DollarVariables;
        public bool PercentVariables;
        /// <summary>Colors <c>\command</c> as a directive (LaTeX).</summary>
        public bool BackslashCommands;
        /// <summary>Colors <c>@rule</c> as a keyword (CSS at-rules, C# style attributes).</summary>
        public bool AtRules;
    }

    private static readonly Dictionary<EditorSyntaxLanguage, Rules> RuleCache = new();
    private static readonly object RuleCacheLock = new();

    private static Rules RulesFor(EditorSyntaxLanguage language)
    {
        lock (RuleCacheLock)
        {
            if (RuleCache.TryGetValue(language, out var cached))
                return cached;

            var rules = BuildRules(language);
            RuleCache[language] = rules;
            return rules;
        }
    }

    private static HashSet<string> Words(params string[] words) => new(words, StringComparer.Ordinal);

    private static HashSet<string> WordsIgnoreCase(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);

    private static Rules BuildRules(EditorSyntaxLanguage language) => language switch
    {
        EditorSyntaxLanguage.Cpp => new Rules
        {
            LineComments = new[] { "//" },
            BlockComment = ("/*", "*/"),
            Preprocessor = true,
            Keywords = Words(
                "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor", "bool", "break", "case",
                "catch", "char", "char8_t", "char16_t", "char32_t", "class", "compl", "concept", "const",
                "const_cast", "consteval", "constexpr", "constinit", "continue", "co_await", "co_return",
                "co_yield", "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum",
                "explicit", "export", "extern", "false", "float", "for", "friend", "goto", "if", "inline", "int",
                "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq", "nullptr", "operator", "or",
                "or_eq", "private", "protected", "public", "register", "reinterpret_cast", "requires", "return",
                "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
                "template", "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename",
                "union", "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while", "xor", "xor_eq",
                // C (C89..C23) keywords and common library typedefs/macros
                "restrict", "_Alignas", "_Alignof", "_Atomic", "_Bool", "_Complex", "_Generic", "_Imaginary",
                "_Noreturn", "_Static_assert", "_Thread_local", "typeof", "typeof_unqual", "NULL", "size_t",
                "ssize_t", "ptrdiff_t", "intptr_t", "uintptr_t", "int8_t", "int16_t", "int32_t", "int64_t",
                "uint8_t", "uint16_t", "uint32_t", "uint64_t", "wchar_t", "va_list", "FILE", "bool", "true",
                "false", "nullptr_t", "std", "string"),
        },
        EditorSyntaxLanguage.CSharp => new Rules
        {
            LineComments = new[] { "//" },
            BlockComment = ("/*", "*/"),
            Keywords = Words(
                "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
                "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double",
                "dynamic", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float",
                "for", "foreach", "get", "global", "goto", "if", "implicit", "in", "init", "int", "interface",
                "internal", "is", "lock", "long", "nameof", "namespace", "new", "nint", "not", "null", "nuint",
                "object", "operator", "out", "override", "params", "partial", "private", "protected", "public",
                "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "set", "short", "sizeof",
                "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
                "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "value", "var", "virtual", "void",
                "volatile", "when", "where", "while", "with", "yield"),
        },
        EditorSyntaxLanguage.Java => new Rules
        {
            LineComments = new[] { "//" },
            BlockComment = ("/*", "*/"),
            AtRules = true,
            Keywords = Words(
                "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
                "continue", "default", "do", "double", "else", "enum", "extends", "final", "finally", "float",
                "for", "goto", "if", "implements", "import", "instanceof", "int", "interface", "long", "native",
                "new", "null", "package", "private", "protected", "public", "record", "return", "sealed", "short",
                "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient",
                "true", "false", "try", "var", "void", "volatile", "while", "yield", "String"),
        },
        EditorSyntaxLanguage.Javascript => new Rules
        {
            LineComments = new[] { "//" },
            BlockComment = ("/*", "*/"),
            BacktickStrings = true,
            Keywords = Words(
                "as", "async", "await", "break", "case", "catch", "class", "const", "constructor", "continue",
                "debugger", "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally",
                "for", "from", "function", "get", "if", "implements", "import", "in", "instanceof", "interface",
                "let", "new", "null", "of", "package", "private", "protected", "public", "readonly", "return",
                "satisfies", "set", "static", "super", "switch", "this", "throw", "true", "try", "type", "typeof",
                "undefined", "var", "void", "while", "with", "yield",
                // TypeScript-flavored primitives (the resolver maps .ts/.tsx here too)
                "any", "boolean", "never", "number", "string", "symbol", "unknown", "bigint", "object", "declare",
                "namespace", "abstract", "keyof", "infer", "is", "asserts"),
        },
        EditorSyntaxLanguage.PHP => new Rules
        {
            LineComments = new[] { "//", "#" },
            BlockComment = ("/*", "*/"),
            DollarVariables = true,
            Keywords = Words(
                "abstract", "and", "array", "as", "break", "callable", "case", "catch", "class", "clone", "const",
                "continue", "declare", "default", "do", "echo", "else", "elseif", "empty", "enddeclare", "endfor",
                "endforeach", "endif", "endswitch", "endwhile", "enum", "extends", "final", "finally", "fn", "for",
                "foreach", "function", "global", "goto", "if", "implements", "include", "include_once", "instanceof",
                "insteadof", "interface", "isset", "list", "match", "namespace", "new", "or", "print", "private",
                "protected", "public", "readonly", "require", "require_once", "return", "static", "switch", "throw",
                "trait", "try", "unset", "use", "var", "while", "xor", "yield", "true", "false", "null"),
        },
        EditorSyntaxLanguage.QSharp => new Rules
        {
            LineComments = new[] { "//" },
            Keywords = Words(
                "namespace", "open", "operation", "function", "newtype", "body", "adjoint", "controlled", "let",
                "mutable", "set", "if", "elif", "else", "for", "in", "while", "repeat", "until", "fixup", "within",
                "apply", "return", "fail", "use", "using", "borrow", "borrowing", "new", "not", "and", "or", "true",
                "false", "Unit", "Int", "BigInt", "Double", "Bool", "String", "Qubit", "Result", "Pauli", "Range",
                "One", "Zero", "PauliI", "PauliX", "PauliY", "PauliZ", "internal", "is", "Adj", "Ctl"),
        },
        EditorSyntaxLanguage.Python => new Rules
        {
            LineComments = new[] { "#" },
            Keywords = Words(
                "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
                "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda",
                "match", "case", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with",
                "yield", "True", "False", "None", "self", "cls"),
        },
        EditorSyntaxLanguage.Lua => new Rules
        {
            LineComments = new[] { "--" },
            BlockComment = ("--[[", "]]"),
            Keywords = Words(
                "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
                "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while", "self"),
        },
        EditorSyntaxLanguage.SQL => new Rules
        {
            LineComments = new[] { "--" },
            BlockComment = ("/*", "*/"),
            DoubleQuoteStrings = false,
            Keywords = WordsIgnoreCase(
                "add", "all", "alter", "and", "any", "as", "asc", "begin", "between", "by", "case", "cast",
                "check", "column", "commit", "constraint", "create", "cross", "database", "declare", "default",
                "delete", "desc", "distinct", "drop", "else", "end", "exec", "exists", "foreign", "from", "full",
                "group", "having", "if", "in", "index", "inner", "insert", "into", "is", "join", "key", "left",
                "like", "limit", "not", "null", "offset", "on", "or", "order", "outer", "primary", "procedure",
                "right", "rollback", "select", "set", "table", "then", "top", "transaction", "truncate", "union",
                "unique", "update", "values", "view", "when", "where", "with", "int", "varchar", "nvarchar",
                "text", "date", "datetime", "bit", "decimal", "float", "identity"),
        },
        EditorSyntaxLanguage.CSS => new Rules
        {
            BlockComment = ("/*", "*/"),
            AtRules = true,
            KeyValueKeys = true,
            Keywords = WordsIgnoreCase(
                "important", "inherit", "initial", "unset", "none", "auto", "flex", "grid", "block", "inline",
                "absolute", "relative", "fixed", "sticky", "hidden", "visible", "bold", "italic", "center",
                "solid", "dashed", "dotted", "transparent", "var", "calc", "rgba", "rgb", "hsl"),
        },
        EditorSyntaxLanguage.Json => new Rules
        {
            SingleQuoteStrings = false,
            JsonPropertyNames = true,
            Keywords = Words("true", "false", "null"),
        },
        EditorSyntaxLanguage.Toml => new Rules
        {
            LineComments = new[] { "#" },
            SectionHeaders = true,
            KeyValueKeys = true,
            Keywords = Words("true", "false"),
        },
        EditorSyntaxLanguage.Inifile => new Rules
        {
            LineComments = new[] { ";", "#" },
            SectionHeaders = true,
            KeyValueKeys = true,
            Keywords = WordsIgnoreCase("true", "false", "yes", "no", "on", "off"),
        },
        EditorSyntaxLanguage.Batch => new Rules
        {
            LineComments = new[] { "::" },
            WordLineComments = new[] { "rem" },
            SingleQuoteStrings = false,
            PercentVariables = true,
            BackslashEscapes = false,
            Keywords = WordsIgnoreCase(
                "call", "cd", "chdir", "cls", "copy", "del", "dir", "echo", "else", "endlocal", "equ", "exit",
                "for", "goto", "if", "in", "md", "mkdir", "move", "not", "pause", "popd", "pushd", "rd", "ren",
                "rmdir", "set", "setlocal", "shift", "start", "timeout", "title", "type", "verify", "xcopy",
                "defined", "errorlevel", "exist", "do", "off", "on"),
        },
        EditorSyntaxLanguage.Gitignore => new Rules
        {
            LineComments = new[] { "#" },
            DoubleQuoteStrings = false,
            SingleQuoteStrings = false,
            Numbers = false,
        },
        EditorSyntaxLanguage.Latex => new Rules
        {
            LineComments = new[] { "%" },
            DoubleQuoteStrings = false,
            SingleQuoteStrings = false,
            BackslashCommands = true,
            Numbers = false,
        },
        EditorSyntaxLanguage.X86Assembly => new Rules
        {
            LineComments = new[] { ";", "#" },
            Keywords = WordsIgnoreCase(
                "mov", "movzx", "movsx", "lea", "push", "pop", "add", "sub", "mul", "imul", "div", "idiv", "inc",
                "dec", "and", "or", "xor", "not", "neg", "shl", "shr", "sar", "cmp", "test", "jmp", "je", "jne",
                "jz", "jnz", "jg", "jge", "jl", "jle", "ja", "jae", "jb", "jbe", "call", "ret", "nop", "int",
                "syscall", "leave", "hlt", "loop", "rep", "movs", "stos", "section", "global", "extern", "db",
                "dw", "dd", "dq", "byte", "word", "dword", "qword", "ptr", "eax", "ebx", "ecx", "edx", "esi",
                "edi", "esp", "ebp", "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rsp", "rbp", "al", "bl", "cl",
                "dl", "ah", "bh", "ch", "dh", "ax", "bx", "cx", "dx"),
        },
        EditorSyntaxLanguage.GCode => new Rules
        {
            LineComments = new[] { ";" },
            BlockComment = ("(", ")"),
            DoubleQuoteStrings = false,
            SingleQuoteStrings = false,
        },
        EditorSyntaxLanguage.HexFile => new Rules
        {
            DoubleQuoteStrings = false,
            SingleQuoteStrings = false,
        },
        EditorSyntaxLanguage.CSVImproved => new Rules
        {
            SingleQuoteStrings = false,
            BackslashEscapes = false,
        },
        _ => new Rules(),
    };

    // ─────────────────────────────── generic code scanner ───────────────────────────────

    private static void ScanCode(string text, Rules rules, List<Token> tokens, int maxTokens)
    {
        int lineStart = 0;
        int i = 0;

        while (i < text.Length && tokens.Count < maxTokens)
        {
            char c = text[i];

            if (c == '\n')
            {
                i++;
                lineStart = i;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Line comments run to the end of the physical line.
            if (TryMatchAny(text, i, rules.LineComments, out int commentMarkerLength))
            {
                int end = EndOfLine(text, i);
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Comment));
                i = end;
                _ = commentMarkerLength;
                continue;
            }

            // Block comments: colored to the terminator, or to end of line when unterminated
            // (the preview has no cross-line state).
            if (rules.BlockComment is { } block && StartsAt(text, i, block.Start))
            {
                int close = text.IndexOf(block.End, i + block.Start.Length, StringComparison.Ordinal);
                int end = close >= 0 ? close + block.End.Length : EndOfLine(text, i);
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Comment));
                i = end;
                continue;
            }

            if (rules.Preprocessor && c == '#' && IsLineIndent(text, lineStart, i))
            {
                i = ScanPreprocessorDirective(text, i, tokens, maxTokens);
                continue;
            }

            if (rules.SectionHeaders && c == '[' && IsLineIndent(text, lineStart, i))
            {
                int close = text.IndexOf(']', i + 1);
                int end = close >= 0 ? close + 1 : EndOfLine(text, i);
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Tag));
                i = end;
                continue;
            }

            if (rules.BackslashCommands && c == '\\' && i + 1 < text.Length && IsIdentifierStart(text[i + 1]))
            {
                int end = i + 1;
                while (end < text.Length && char.IsLetter(text[end])) end++;
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Preprocessor));
                i = end;
                continue;
            }

            if (rules.AtRules && c == '@' && i + 1 < text.Length && IsIdentifierStart(text[i + 1]))
            {
                int end = i + 1;
                while (end < text.Length && IsIdentifierPart(text[end])) end++;
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Keyword));
                i = end;
                continue;
            }

            if (rules.DollarVariables && c == '$' && i + 1 < text.Length && IsIdentifierStart(text[i + 1]))
            {
                int end = i + 1;
                while (end < text.Length && IsIdentifierPart(text[end])) end++;
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Attribute));
                i = end;
                continue;
            }

            if (rules.PercentVariables && c == '%')
            {
                int close = text.IndexOf('%', i + 1);
                if (close > i + 1 && close - i <= 64)
                {
                    tokens.Add(new Token(i, close - i + 1, PreviewTokenKind.Attribute));
                    i = close + 1;
                    continue;
                }
            }

            if (IsStringDelimiter(c, rules))
            {
                int end = ScanString(text, i, rules.BackslashEscapes);
                var kind = PreviewTokenKind.StringLiteral;
                if (rules.JsonPropertyNames && IsFollowedByColon(text, end))
                    kind = PreviewTokenKind.Tag;
                tokens.Add(new Token(i, end - i, kind));
                i = end;
                continue;
            }

            if (rules.Numbers && char.IsDigit(c) && !IsIdentifierPart(CharAt(text, i - 1)))
            {
                int end = ScanNumber(text, i);
                tokens.Add(new Token(i, end - i, PreviewTokenKind.Number));
                i = end;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                int end = i + 1;
                while (end < text.Length && IsIdentifierPart(text[end])) end++;
                string word = text[i..end];

                if (rules.WordLineComments.Length > 0 && ContainsIgnoreCase(rules.WordLineComments, word))
                {
                    int lineEnd = EndOfLine(text, i);
                    tokens.Add(new Token(i, lineEnd - i, PreviewTokenKind.Comment));
                    i = lineEnd;
                    continue;
                }

                if (rules.Keywords is { } keywords && keywords.Contains(word))
                {
                    tokens.Add(new Token(i, end - i, PreviewTokenKind.Keyword));
                    i = end;
                    continue;
                }

                if (rules.KeyValueKeys && IsKeyPosition(text, lineStart, i) && IsFollowedByAssignment(text, end))
                {
                    tokens.Add(new Token(i, end - i, PreviewTokenKind.Attribute));
                    i = end;
                    continue;
                }

                i = end;
                continue;
            }

            i++;
        }
    }

    /// <summary>
    /// Colors a C/C++ <c>#directive</c> and, for includes, the <c>&lt;header&gt;</c> target.
    /// Returns the index to continue scanning from.
    /// </summary>
    private static int ScanPreprocessorDirective(string text, int hashIndex, List<Token> tokens, int maxTokens)
    {
        int i = hashIndex + 1;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;

        int wordStart = i;
        while (i < text.Length && char.IsLetter(text[i])) i++;

        tokens.Add(new Token(hashIndex, Math.Max(1, i - hashIndex), PreviewTokenKind.Preprocessor));

        bool isInclude = i > wordStart
            && (text.AsSpan(wordStart, i - wordStart).SequenceEqual("include")
                || text.AsSpan(wordStart, i - wordStart).SequenceEqual("import")
                || text.AsSpan(wordStart, i - wordStart).SequenceEqual("include_next"));

        if (isInclude && tokens.Count < maxTokens)
        {
            int j = i;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
            if (j < text.Length && text[j] == '<')
            {
                int close = text.IndexOf('>', j + 1);
                int end = close >= 0 ? close + 1 : EndOfLine(text, j);
                tokens.Add(new Token(j, end - j, PreviewTokenKind.StringLiteral));
                return end;
            }
        }

        return i;
    }

    // ─────────────────────────────── markup scanner (HTML / XML) ───────────────────────────────

    private static void ScanMarkup(string text, List<Token> tokens, int maxTokens)
    {
        int i = 0;
        while (i < text.Length && tokens.Count < maxTokens)
        {
            int open = text.IndexOf('<', i);
            if (open < 0)
                break;

            if (StartsAt(text, open, "<!--"))
            {
                int close = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                int end = close >= 0 ? close + 3 : EndOfLine(text, open);
                tokens.Add(new Token(open, end - open, PreviewTokenKind.Comment));
                i = end;
                continue;
            }

            int tagEnd = text.IndexOf('>', open + 1);
            int limit = tagEnd >= 0 ? tagEnd + 1 : EndOfLine(text, open);

            int nameStart = open + 1;
            while (nameStart < limit && (text[nameStart] == '/' || text[nameStart] == '!' || text[nameStart] == '?'))
                nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < limit && (IsIdentifierPart(text[nameEnd]) || text[nameEnd] == ':' || text[nameEnd] == '-' || text[nameEnd] == '.'))
                nameEnd++;

            if (nameEnd > nameStart)
                tokens.Add(new Token(open, nameEnd - open, PreviewTokenKind.Tag));

            int k = nameEnd;
            while (k < limit && tokens.Count < maxTokens)
            {
                char c = text[k];
                if (c == '"' || c == '\'')
                {
                    int end = ScanString(text, k, backslashEscapes: false);
                    tokens.Add(new Token(k, Math.Min(end, limit) - k, PreviewTokenKind.StringLiteral));
                    k = end;
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    int end = k + 1;
                    while (end < limit && (IsIdentifierPart(text[end]) || text[end] == '-' || text[end] == ':')) end++;
                    tokens.Add(new Token(k, end - k, PreviewTokenKind.Attribute));
                    k = end;
                    continue;
                }

                k++;
            }

            i = Math.Max(limit, nameEnd);
        }
    }

    // ─────────────────────────────── Markdown scanner ───────────────────────────────

    private static void ScanMarkdown(string text, List<Token> tokens, int maxTokens)
    {
        int i = 0;
        int indent = 0;
        while (indent < text.Length && (text[indent] == ' ' || text[indent] == '\t')) indent++;

        if (indent < text.Length)
        {
            char first = text[indent];
            if (first == '#')
            {
                tokens.Add(new Token(indent, EndOfLine(text, indent) - indent, PreviewTokenKind.Tag));
                return;
            }
            if (first == '>')
            {
                tokens.Add(new Token(indent, EndOfLine(text, indent) - indent, PreviewTokenKind.Comment));
                return;
            }
            if (StartsAt(text, indent, "```") || StartsAt(text, indent, "~~~"))
            {
                tokens.Add(new Token(indent, EndOfLine(text, indent) - indent, PreviewTokenKind.Preprocessor));
                return;
            }
            if ((first == '-' || first == '*' || first == '+')
                && indent + 1 < text.Length && text[indent + 1] == ' ')
            {
                tokens.Add(new Token(indent, 1, PreviewTokenKind.Keyword));
                i = indent + 1;
            }
        }

        while (i < text.Length && tokens.Count < maxTokens)
        {
            char c = text[i];

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                int end = close >= 0 ? close + 1 : EndOfLine(text, i);
                tokens.Add(new Token(i, end - i, PreviewTokenKind.StringLiteral));
                i = end;
                continue;
            }

            if (c == '(' && i > 0 && text[i - 1] == ']')
            {
                int close = text.IndexOf(')', i + 1);
                if (close > i)
                {
                    tokens.Add(new Token(i + 1, close - i - 1, PreviewTokenKind.Attribute));
                    i = close + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > i)
                {
                    tokens.Add(new Token(i, close - i + 1, PreviewTokenKind.Keyword));
                    i = close + 1;
                    continue;
                }
            }

            i++;
        }
    }

    // ─────────────────────────────── primitives ───────────────────────────────

    private static bool IsStringDelimiter(char c, Rules rules) =>
        (c == '"' && rules.DoubleQuoteStrings)
        || (c == '\'' && rules.SingleQuoteStrings)
        || (c == '`' && rules.BacktickStrings);

    /// <summary>Scans a quoted run; an unterminated quote is colored to the end of the line.</summary>
    private static int ScanString(string text, int start, bool backslashEscapes)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
                return i;
            if (backslashEscapes && c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }
            if (c == quote)
                return i + 1;
            i++;
        }
        return text.Length;
    }

    private static int ScanNumber(string text, int start)
    {
        int i = start;
        if (text[i] == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X' || text[i + 1] == 'b' || text[i + 1] == 'B'))
            i += 2;

        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
            {
                // Exponent sign (1e-9) stays part of the literal.
                if ((c == 'e' || c == 'E') && i + 1 < text.Length && (text[i + 1] == '+' || text[i + 1] == '-'))
                {
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    private static int EndOfLine(string text, int from)
    {
        int newline = text.IndexOf('\n', from);
        return newline < 0 ? text.Length : newline;
    }

    private static bool StartsAt(string text, int index, string value) =>
        index >= 0 && index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool TryMatchAny(string text, int index, string[] markers, out int length)
    {
        foreach (var marker in markers)
        {
            if (StartsAt(text, index, marker))
            {
                length = marker.Length;
                return true;
            }
        }
        length = 0;
        return false;
    }

    private static bool ContainsIgnoreCase(string[] values, string word)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when everything between <paramref name="lineStart"/> and <paramref name="index"/> is blank.</summary>
    private static bool IsLineIndent(string text, int lineStart, int index)
    {
        for (int i = lineStart; i < index; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }
        return true;
    }

    private static bool IsKeyPosition(string text, int lineStart, int index) => IsLineIndent(text, lineStart, index);

    private static bool IsFollowedByAssignment(string text, int index)
    {
        for (int i = index; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' || c == '\t') continue;
            return c == '=' || c == ':';
        }
        return false;
    }

    private static bool IsFollowedByColon(string text, int index)
    {
        for (int i = index; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' || c == '\t') continue;
            return c == ':';
        }
        return false;
    }

    private static char CharAt(string text, int index) => index >= 0 && index < text.Length ? text[index] : '\0';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
