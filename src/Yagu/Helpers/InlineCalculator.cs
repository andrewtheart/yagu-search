using System.Globalization;

namespace Yagu.Helpers;

/// <summary>What kind of inline answer <see cref="InlineCalculator"/> produced.</summary>
public enum InlineCalcKind
{
    /// <summary>An arithmetic expression such as <c>2 + 2</c> or <c>15% of 340</c>.</summary>
    Arithmetic,

    /// <summary>A unit conversion such as <c>5 km to miles</c>.</summary>
    UnitConversion,
}

/// <summary>
/// The result of evaluating a search-box expression inline.
/// </summary>
/// <param name="Display">Human-readable summary, e.g. <c>"5 km = 3.106856 miles"</c>.</param>
/// <param name="Value">Just the answer, suitable for copying, e.g. <c>"3.106856"</c>.</param>
/// <param name="Kind">Whether the answer is arithmetic or a unit conversion.</param>
public sealed record InlineCalcResult(string Display, string Value, InlineCalcKind Kind);

/// <summary>
/// A tiny, dependency-free calculator and unit converter for the search box. When the typed query is
/// a recognizable arithmetic expression (<c>2+2</c>, <c>sqrt(9)*4</c>, <c>15% of 340</c>) or a unit
/// conversion (<c>5 km to miles</c>, <c>72 f to c</c>), the app shows the answer inline instead of —
/// or in addition to — running a normal search. All methods are pure so they are unit-testable and
/// safe to call on every keystroke.
/// </summary>
public static class InlineCalculator
{
    /// <summary>Guards the recursive-descent parser against a stack overflow on pathological input
    /// (deeply nested parentheses recurse one frame per level). No real calculation nests this deep.</summary>
    private const int MaxParenDepth = 128;

    /// <summary>
    /// Evaluates <paramref name="input"/> as a unit conversion first, then as an arithmetic
    /// expression. Returns null when the text is not a recognizable expression, so callers can fall
    /// back to an ordinary search.
    /// </summary>
    public static InlineCalcResult? Evaluate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // A unit conversion is more specific (it needs a " to "/" in " separator with real units), so
        // try it before generic arithmetic.
        if (TryUnitConvert(input) is { } conversion)
            return conversion;

        // Don't turn a plain number search (e.g. "123" or "-5") into a "123 = 123" banner — only show
        // the calculator when the query actually computes something.
        if (IsBareNumber(input))
            return null;

        if (TryCalc(input) is { } value)
        {
            string formatted = FormatNumber(value);
            return new InlineCalcResult($"{input.Trim()} = {formatted}", formatted, InlineCalcKind.Arithmetic);
        }

        return null;
    }

    private static bool IsBareNumber(string input)
    {
        string s = input.Trim();
        if (s.Length == 0)
            return false;
        int i = 0;
        if (s[0] is '+' or '-')
            i++;
        bool anyDigit = false, dotSeen = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c)) { anyDigit = true; continue; }
            if (c == '.' && !dotSeen) { dotSeen = true; continue; }
            return false;
        }
        return anyDigit;
    }

    // ── Arithmetic ──────────────────────────────────────────────────────────

    /// <summary>Evaluates a pure arithmetic expression, returning null when the text is not one.</summary>
    public static double? TryCalc(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string s = input.Trim();
        // Must contain at least one digit to be a math expression.
        if (!s.Any(char.IsAsciiDigit))
            return null;

        // Handle the "X% of Y" shorthand up front.
        if (TryPercentOf(s) is { } pctOf)
            return pctOf;

        var tokens = Tokenize(s);
        if (tokens is null)
            return null;

        int depth = 0, maxDepth = 0;
        foreach (var t in tokens)
        {
            if (t.Type == TokenType.LParen) { depth++; maxDepth = Math.Max(maxDepth, depth); }
            else if (t.Type == TokenType.RParen) depth--;
        }
        if (maxDepth > MaxParenDepth)
            return null;

        int pos = 0;
        double? result = ParseExpr(tokens, ref pos);
        if (result is null)
            return null;

        // Any remaining non-EOF token means the input wasn't a clean expression (e.g. "5 km").
        while (pos < tokens.Count)
        {
            if (tokens[pos].Type != TokenType.Eof)
                return null;
            pos++;
        }

        double value = result.Value;
        if (double.IsNaN(value) || double.IsInfinity(value))
            return null;
        return value;
    }

    private static double? TryPercentOf(string s)
    {
        // Match "N% of M" case-insensitively on the original bytes so slicing stays valid.
        const string needle = "% of ";
        int idx = s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        string pctStr = s[..idx].Trim();
        string restStr = s[(idx + needle.Length)..].Trim();
        if (!double.TryParse(pctStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            return null;
        if (!double.TryParse(restStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double baseVal))
            return null;
        return pct / 100.0 * baseVal;
    }

    private enum TokenType { Num, Plus, Minus, Star, Slash, Caret, Percent, LParen, RParen, Ident, Eof }

    private readonly record struct Token(TokenType Type, double Number = 0, string Text = "");

    private static List<Token>? Tokenize(string s)
    {
        var chars = s.ToCharArray();
        var tokens = new List<Token>();
        int i = 0;
        while (i < chars.Length)
        {
            char c = chars[i];
            switch (c)
            {
                case ' ':
                case '\t':
                    i++;
                    break;
                case '+': tokens.Add(new Token(TokenType.Plus)); i++; break;
                case '-': tokens.Add(new Token(TokenType.Minus)); i++; break;
                case '*':
                case '\u00D7': // ×
                    tokens.Add(new Token(TokenType.Star)); i++; break;
                case '/':
                case '\u00F7': // ÷
                    tokens.Add(new Token(TokenType.Slash)); i++; break;
                case '^': tokens.Add(new Token(TokenType.Caret)); i++; break;
                case '%': tokens.Add(new Token(TokenType.Percent)); i++; break;
                case '(': tokens.Add(new Token(TokenType.LParen)); i++; break;
                case ')': tokens.Add(new Token(TokenType.RParen)); i++; break;
                case ',': i++; break; // ignore thousands separators
                default:
                    if (char.IsAsciiDigit(c) || c == '.')
                    {
                        int start = i;
                        while (i < chars.Length && (char.IsAsciiDigit(chars[i]) || chars[i] == '.'))
                            i++;
                        string numStr = new string(chars, start, i - start);
                        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                            return null;
                        tokens.Add(new Token(TokenType.Num, n));
                    }
                    else if (char.IsLetter(c) || c == '_')
                    {
                        int start = i;
                        while (i < chars.Length && (char.IsLetterOrDigit(chars[i]) || chars[i] == '_'))
                            i++;
                        string word = new string(chars, start, i - start).ToLowerInvariant();
                        tokens.Add(new Token(TokenType.Ident, Text: word));
                    }
                    else
                    {
                        return null; // unknown character → not a math expression
                    }
                    break;
            }
        }
        tokens.Add(new Token(TokenType.Eof));
        return tokens;
    }

    // expr = term (('+' | '-') term)*
    private static double? ParseExpr(List<Token> tokens, ref int pos)
    {
        double? left = ParseTerm(tokens, ref pos);
        if (left is null)
            return null;
        while (true)
        {
            TokenType t = Peek(tokens, pos);
            if (t == TokenType.Plus)
            {
                pos++;
                double? r = ParseTerm(tokens, ref pos);
                if (r is null) return null;
                left += r;
            }
            else if (t == TokenType.Minus)
            {
                pos++;
                double? r = ParseTerm(tokens, ref pos);
                if (r is null) return null;
                left -= r;
            }
            else
            {
                break;
            }
        }
        return left;
    }

    // term = power (('*' | '/' | '%') power)*
    private static double? ParseTerm(List<Token> tokens, ref int pos)
    {
        double? left = ParsePower(tokens, ref pos);
        if (left is null)
            return null;
        while (true)
        {
            TokenType t = Peek(tokens, pos);
            if (t == TokenType.Star)
            {
                pos++;
                double? r = ParsePower(tokens, ref pos);
                if (r is null) return null;
                left *= r;
            }
            else if (t == TokenType.Slash)
            {
                pos++;
                double? r = ParsePower(tokens, ref pos);
                if (r is null) return null;
                if (r.Value == 0.0) return null;
                left /= r;
            }
            else if (t == TokenType.Percent)
            {
                pos++;
                if (Peek(tokens, pos) == TokenType.Ident && tokens[pos].Text == "of")
                {
                    pos++;
                    double? baseVal = ParsePower(tokens, ref pos);
                    if (baseVal is null) return null;
                    left = left / 100.0 * baseVal;
                }
                else
                {
                    left /= 100.0;
                }
            }
            else
            {
                break;
            }
        }
        return left;
    }

    // power = unary ('^' power)?  (right-associative)
    private static double? ParsePower(List<Token> tokens, ref int pos)
    {
        double? baseVal = ParseUnary(tokens, ref pos);
        if (baseVal is null)
            return null;
        if (Peek(tokens, pos) == TokenType.Caret)
        {
            pos++;
            double? exp = ParsePower(tokens, ref pos);
            if (exp is null) return null;
            return Math.Pow(baseVal.Value, exp.Value);
        }
        return baseVal;
    }

    // unary = '-' unary | primary
    private static double? ParseUnary(List<Token> tokens, ref int pos)
    {
        if (Peek(tokens, pos) == TokenType.Minus)
        {
            pos++;
            double? v = ParseUnary(tokens, ref pos);
            return v is null ? null : -v;
        }
        return ParsePrimary(tokens, ref pos);
    }

    // primary = number | ident '(' expr ')' | ident | '(' expr ')'
    private static double? ParsePrimary(List<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count)
            return null;
        Token tok = tokens[pos];
        switch (tok.Type)
        {
            case TokenType.Num:
                pos++;
                return tok.Number;
            case TokenType.LParen:
                pos++;
                double? val = ParseExpr(tokens, ref pos);
                if (val is null) return null;
                if (Peek(tokens, pos) == TokenType.RParen)
                    pos++;
                return val;
            case TokenType.Ident:
                pos++;
                if (Peek(tokens, pos) == TokenType.LParen)
                {
                    pos++;
                    double? arg = ParseExpr(tokens, ref pos);
                    if (arg is null) return null;
                    if (Peek(tokens, pos) == TokenType.RParen)
                        pos++;
                    return tok.Text switch
                    {
                        "sqrt" => Math.Sqrt(arg.Value),
                        "abs" => Math.Abs(arg.Value),
                        "round" => Math.Round(arg.Value, MidpointRounding.AwayFromZero),
                        "floor" => Math.Floor(arg.Value),
                        "ceil" => Math.Ceiling(arg.Value),
                        "sin" => Math.Sin(DegreesToRadians(arg.Value)),
                        "cos" => Math.Cos(DegreesToRadians(arg.Value)),
                        "tan" => Math.Tan(DegreesToRadians(arg.Value)),
                        "log" => Math.Log10(arg.Value),
                        "ln" => Math.Log(arg.Value),
                        _ => null,
                    };
                }
                return tok.Text switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    _ => (double?)null,
                };
            default:
                return null;
        }
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static TokenType Peek(List<Token> tokens, int pos) =>
        pos >= 0 && pos < tokens.Count ? tokens[pos].Type : TokenType.Eof;

    // ── Unit conversion ─────────────────────────────────────────────────────

    private readonly record struct UnitEntry(string[] Aliases, string BaseUnit, double ToBase, string Category);

    private static readonly UnitEntry[] UnitTable =
    [
        new(["mm", "millimeter", "millimeters"], "m", 0.001, "len"),
        new(["cm", "centimeter", "centimeters"], "m", 0.01, "len"),
        new(["m", "meter", "meters"], "m", 1.0, "len"),
        new(["km", "kilometer", "kilometers"], "m", 1000.0, "len"),
        new(["in", "inch", "inches"], "m", 0.0254, "len"),
        new(["ft", "foot", "feet"], "m", 0.3048, "len"),
        new(["yd", "yard", "yards"], "m", 0.9144, "len"),
        new(["mi", "mile", "miles"], "m", 1609.344, "len"),
        new(["mg", "milligram", "milligrams"], "kg", 0.000001, "mass"),
        new(["g", "gram", "grams"], "kg", 0.001, "mass"),
        new(["kg", "kilogram", "kilograms"], "kg", 1.0, "mass"),
        new(["lb", "lbs", "pound", "pounds"], "kg", 0.453592, "mass"),
        new(["oz", "ounce", "ounces"], "kg", 0.0283495, "mass"),
        new(["t", "tonne", "tonnes"], "kg", 1000.0, "mass"),
        new(["b", "byte", "bytes"], "b", 1.0, "data"),
        new(["kb", "kilobyte", "kilobytes"], "b", 1024.0, "data"),
        new(["mb", "megabyte", "megabytes"], "b", 1048576.0, "data"),
        new(["gb", "gigabyte", "gigabytes"], "b", 1073741824.0, "data"),
        new(["tb", "terabyte", "terabytes"], "b", 1099511627776.0, "data"),
        new(["kph", "kmh", "km/h"], "ms", 0.277778, "speed"),
        new(["mph"], "ms", 0.44704, "speed"),
        new(["m/s", "mps"], "ms", 1.0, "speed"),
        new(["s", "sec", "second", "seconds"], "s", 1.0, "time"),
        new(["min", "minute", "minutes"], "s", 60.0, "time"),
        new(["h", "hr", "hour", "hours"], "s", 3600.0, "time"),
        new(["d", "day", "days"], "s", 86400.0, "time"),
        new(["week", "weeks"], "s", 604800.0, "time"),
    ];

    /// <summary>Converts <paramref name="input"/> between units, returning null when it is not a
    /// recognizable "&lt;number&gt;&lt;from&gt; to &lt;to&gt;" conversion.</summary>
    public static InlineCalcResult? TryUnitConvert(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string s = input.Trim().ToLowerInvariant();
        string sep;
        if (s.Contains(" to "))
            sep = " to ";
        else if (s.Contains(" in "))
            sep = " in ";
        else
            return null;

        int sepIdx = s.IndexOf(sep, StringComparison.Ordinal);
        string left = s[..sepIdx].Trim();
        string toUnit = s[(sepIdx + sep.Length)..].Trim();

        int numEnd = 0;
        while (numEnd < left.Length && (char.IsAsciiDigit(left[numEnd]) || left[numEnd] == '.' || left[numEnd] == '-'))
            numEnd++;
        if (numEnd == 0)
            return null;
        if (!double.TryParse(left[..numEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return null;
        string fromUnit = left[numEnd..].Trim();

        // Temperature has an affine (not linear) relationship, so handle it separately.
        if (TemperatureCode(fromUnit) is { } tf && TemperatureCode(toUnit) is { } tt)
        {
            double celsius = tf switch
            {
                'C' => num,
                'F' => (num - 32.0) * 5.0 / 9.0,
                'K' => num - 273.15,
                _ => double.NaN,
            };
            double converted = tt switch
            {
                'C' => celsius,
                'F' => celsius * 9.0 / 5.0 + 32.0,
                'K' => celsius + 273.15,
                _ => double.NaN,
            };
            if (double.IsNaN(converted))
                return null;
            string dt = FormatNumber(converted);
            return new InlineCalcResult(
                $"{FormatNumber(num)} {tf} = {dt} {tt}", dt, InlineCalcKind.UnitConversion);
        }

        var from = Lookup(fromUnit);
        var to = Lookup(toUnit);
        if (from is null || to is null)
            return null;
        if (from.Value.BaseUnit != to.Value.BaseUnit || from.Value.Category != to.Value.Category)
            return null;

        double result = (num * from.Value.ToBase) / to.Value.ToBase;
        string d = FormatNumber(result);
        return new InlineCalcResult($"{FormatNumber(num)} {fromUnit} = {d} {toUnit}", d, InlineCalcKind.UnitConversion);
    }

    private static char? TemperatureCode(string u) => u switch
    {
        "c" or "celsius" => 'C',
        "f" or "fahrenheit" => 'F',
        "k" or "kelvin" => 'K',
        _ => null,
    };

    private static UnitEntry? Lookup(string unit)
    {
        foreach (var entry in UnitTable)
        {
            if (Array.IndexOf(entry.Aliases, unit) >= 0)
                return entry;
        }
        return null;
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string FormatNumber(double v)
    {
        if (!double.IsFinite(v))
            return string.Empty;
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        string s = v.ToString("F6", CultureInfo.InvariantCulture);
        return s.TrimEnd('0').TrimEnd('.');
    }
}
