using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LiveHelpDesk.Abstractions;

namespace LiveHelpDesk.Core.Rules;

public enum ExprTokenKind
{
    /// <summary>A field name, a keyword (e, o, non, contiene, tra...) or a literal word (sì, no, vuoto).</summary>
    Word,
    String,
    Number,
    Symbol,

    /// <summary><c>$me</c>: the signed-in user.</summary>
    Me,
    Invalid,
}

/// <summary>What a word means in an expression.</summary>
public enum ExprWordRole
{
    /// <summary>Not a keyword: a field name.</summary>
    None,
    And,
    Or,
    Not,

    /// <summary>contiene, iniziaCon, regex, tra.</summary>
    Operator,

    /// <summary>sì, no, vuoto...</summary>
    Literal,
}

public sealed record ExprToken(ExprTokenKind Kind, int Start, int Length, string Text)
{
    public int End => Start + Length;

    /// <summary>False for a string whose closing quote has not been typed yet.</summary>
    public bool IsClosed { get; init; } = true;

    /// <summary>The text of a string, without quotes and escapes.</summary>
    public string? StringValue { get; init; }

    public ExprWordRole Role => Kind == ExprTokenKind.Word ? RuleExpression.RoleOf(Text) : ExprWordRole.None;
}

/// <param name="Start">Offset in the expression of the part in error.</param>
public sealed record RuleExpressionError(string Message, int Start, int Length)
{
    public string Describe(string text)
    {
        var before = text[..Math.Min(Start, text.Length)];
        var line = before.Count(c => c == '\n') + 1;
        var column = Start - (before.LastIndexOf('\n') + 1) + 1;
        return line > 1 ? $"riga {line}, colonna {column}: {Message}" : $"colonna {column}: {Message}";
    }
}

/// <summary>A parsed expression; <see cref="Error"/> is set when the text is not a valid condition.</summary>
public sealed class ParsedRuleExpression
{
    internal ParsedRuleExpression(ExprNode? root, RuleExpressionError? error, IReadOnlyList<ExprToken> tokens)
    {
        Root = root;
        Error = error;
        Tokens = tokens;
    }

    internal ExprNode? Root { get; }

    public RuleExpressionError? Error { get; }

    public IReadOnlyList<ExprToken> Tokens { get; }

    public bool IsValid => Error is null && Root is not null;

    /// <summary>The condition as a LINQ expression tree, ready to be compiled; null when the text is not valid.</summary>
    public Expression<Func<TicketEvent, EvaluationContext, bool>>? ToLinq() =>
        Root is null || Error is not null ? null : RuleExpression.ToLinq(Root);
}

internal abstract record ExprNode;

internal sealed record AndExpr(ExprNode Left, ExprNode Right) : ExprNode;

internal sealed record OrExpr(ExprNode Left, ExprNode Right) : ExprNode;

internal sealed record NotExpr(ExprNode Inner) : ExprNode;

/// <summary>One comparison, in the rule engine's terms: field key, operator ("eq", "in"...) and operands.</summary>
internal sealed record TestExpr(string Field, string Op, IReadOnlyList<string?> Values) : ExprNode;

public enum ExprCompletionKind
{
    None,
    Field,
    Operator,
    Value,
    Logic,
}

/// <param name="ReplaceStart">Where the text the suggestion replaces starts (up to the caret).</param>
/// <param name="Field">The field key, for operators and values.</param>
/// <param name="Negated">True after "campo non": only the word operators make sense.</param>
public sealed record ExprCompletion(ExprCompletionKind Kind, int ReplaceStart, string? Field = null, bool Negated = false);

/// <summary>
/// The free-text condition of a rule: <c>priorita = "Alta" e (coda = "Supporto L1" o titolo contiene "urgente")</c>.
/// It is parsed into a LINQ expression tree that the rule engine compiles and runs on each event.
/// </summary>
/// <remarks>
/// Grammar (keywords in Italian or English, case-insensitive):
/// <code>
/// espressione := termine (("o" | "or" | "||") termine)*
/// termine     := fattore (("e" | "and" | "&amp;&amp;") fattore)*
/// fattore     := ("non" | "not" | "!") fattore | "(" espressione ")" | confronto
/// confronto   := campo ( ("=" | "!=" | "&gt;" | "&gt;=" | "&lt;" | "&lt;=") valore
///                      | ["non"] ("contiene" | "iniziaCon" | "regex") valore
///                      | ["non"] ("tra" | "in") "(" valore ("," valore)* ")" )
///              | campo_sì_no
/// valore      := "testo" | 'testo' | numero | sì | no | vuoto | $me
/// </code>
/// </remarks>
public static class RuleExpression
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Dictionary<string, ExprWordRole> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["e"] = ExprWordRole.And,
        ["and"] = ExprWordRole.And,
        ["o"] = ExprWordRole.Or,
        ["or"] = ExprWordRole.Or,
        ["non"] = ExprWordRole.Not,
        ["not"] = ExprWordRole.Not,
        ["contiene"] = ExprWordRole.Operator,
        ["contains"] = ExprWordRole.Operator,
        ["iniziacon"] = ExprWordRole.Operator,
        ["inizia_con"] = ExprWordRole.Operator,
        ["startswith"] = ExprWordRole.Operator,
        ["regex"] = ExprWordRole.Operator,
        ["corrisponde"] = ExprWordRole.Operator,
        ["tra"] = ExprWordRole.Operator,
        ["in"] = ExprWordRole.Operator,
        ["sì"] = ExprWordRole.Literal,
        ["si"] = ExprWordRole.Literal,
        ["vero"] = ExprWordRole.Literal,
        ["true"] = ExprWordRole.Literal,
        ["no"] = ExprWordRole.Literal,
        ["falso"] = ExprWordRole.Literal,
        ["false"] = ExprWordRole.Literal,
        ["vuoto"] = ExprWordRole.Literal,
        ["null"] = ExprWordRole.Literal,
    };

    private static readonly string[] Symbols = ["&&", "||", "==", "!=", "<>", ">=", "<=", "=", ">", "<", "!", "(", ")", ","];

    private static readonly ConcurrentDictionary<string, Func<TicketEvent, EvaluationContext, bool>?> Compiled = new();

    private static readonly System.Reflection.MethodInfo TestMethod =
        typeof(RuleEngine).GetMethod(nameof(RuleEngine.Test))!;

    public static ExprWordRole RoleOf(string word) => Words.GetValueOrDefault(word, ExprWordRole.None);

    /// <summary>Splits the text into tokens. Never fails: what cannot be read becomes an <see cref="ExprTokenKind.Invalid"/> token.</summary>
    public static IReadOnlyList<ExprToken> Tokenize(string text)
    {
        var tokens = new List<ExprToken>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (c is '"' or '\'')
            {
                var value = new StringBuilder();
                var closed = false;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == c || text[i + 1] == '\\'))
                    {
                        value.Append(text[i + 1]);
                        i += 2;
                    }
                    else if (text[i] == c)
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    else
                    {
                        value.Append(text[i++]);
                    }
                }

                tokens.Add(new ExprToken(ExprTokenKind.String, start, i - start, text[start..i]) { IsClosed = closed, StringValue = value.ToString() });
            }
            else if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new ExprToken(ExprTokenKind.Number, start, i - start, text[start..i]));
            }
            else if (IsWordStart(c) || c == '$')
            {
                i++;
                while (i < text.Length && IsWordPart(text[i]))
                {
                    i++;
                }

                var word = text[start..i];
                var kind = c != '$' ? ExprTokenKind.Word
                    : word.Equals("$me", StringComparison.OrdinalIgnoreCase) ? ExprTokenKind.Me
                    : ExprTokenKind.Invalid;
                tokens.Add(new ExprToken(kind, start, i - start, word));
            }
            else if (Symbols.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0) is { } symbol)
            {
                i += symbol.Length;
                tokens.Add(new ExprToken(ExprTokenKind.Symbol, start, symbol.Length, symbol));
            }
            else
            {
                i++;
                tokens.Add(new ExprToken(ExprTokenKind.Invalid, start, 1, text[start..i]));
            }
        }

        return tokens;
    }

    public static ParsedRuleExpression Parse(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            return new ParsedRuleExpression(null, new RuleExpressionError("l'espressione è vuota.", 0, 0), tokens);
        }

        try
        {
            var parser = new Parser(tokens, text.Length);
            var root = parser.ParseOr();
            if (!parser.AtEnd)
            {
                var token = parser.Current!;
                throw new ExprException(
                    token.Kind == ExprTokenKind.Symbol && token.Text == ")" ? "parentesi chiusa senza la sua aperta." : $"non mi aspettavo «{token.Text}»: manca «e» oppure «o»?",
                    token.Start,
                    token.Length);
            }

            return new ParsedRuleExpression(root, null, tokens);
        }
        catch (ExprException ex)
        {
            return new ParsedRuleExpression(null, new RuleExpressionError(ex.Message, ex.Start, ex.Length), tokens);
        }
    }

    /// <summary>Evaluates the expression on an event; an expression that is not valid matches nothing.</summary>
    public static bool Evaluate(string text, TicketEvent evt, EvaluationContext context)
    {
        if (Compiled.Count > 1000)
        {
            Compiled.Clear();
        }

        var predicate = Compiled.GetOrAdd(text, static t => Parse(t).ToLinq()?.Compile());
        return predicate is not null && predicate(evt, context);
    }

    internal static Expression<Func<TicketEvent, EvaluationContext, bool>> ToLinq(ExprNode root)
    {
        var evt = Expression.Parameter(typeof(TicketEvent), "evt");
        var context = Expression.Parameter(typeof(EvaluationContext), "context");

        Expression Build(ExprNode node) => node switch
        {
            AndExpr and => Expression.AndAlso(Build(and.Left), Build(and.Right)),
            OrExpr or => Expression.OrElse(Build(or.Left), Build(or.Right)),
            NotExpr not => Expression.Not(Build(not.Inner)),
            TestExpr test => Expression.Call(
                TestMethod,
                Expression.Constant(test.Field),
                Expression.Constant(test.Op),
                Expression.Constant(test.Values, typeof(IReadOnlyList<string?>)),
                evt,
                context),
            _ => throw new InvalidOperationException(),
        };

        return Expression.Lambda<Func<TicketEvent, EvaluationContext, bool>>(Build(root), evt, context);
    }

    /// <summary>
    /// Writes a condition of the rules file as an expression (used to move "almeno una" and "nessuna" conditions into
    /// the expression box). Null when the condition has something an expression cannot say.
    /// </summary>
    public static string? Format(RuleCondition condition)
    {
        var formatted = Format(condition, out _);
        return formatted is not null && Parse(formatted).IsValid ? formatted : null;
    }

    /// <summary>Joins expressions with "e", bracketing those that need it.</summary>
    public static string Join(IEnumerable<string> expressions)
    {
        var parts = expressions.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        return parts.Count == 1 ? parts[0] : string.Join(" e ", parts.Select(p => NeedsBrackets(p) ? $"({p})" : p));
    }

    private static bool NeedsBrackets(string expression) =>
        Tokenize(expression).Any(t => t.Role == ExprWordRole.Or || (t.Kind == ExprTokenKind.Symbol && t.Text == "||"));

    // Precedence: 1 = "o", 2 = "e", 3 = a single comparison or a negation.
    private static string? Format(RuleCondition condition, out int precedence)
    {
        var parts = new List<(string Text, int Precedence)>();

        if (condition.Field is not null || condition.Op is not null)
        {
            if (FormatLeaf(condition) is not { } leaf)
            {
                precedence = 0;
                return null;
            }

            parts.Add((leaf, 3));
        }

        foreach (var (group, joiner, level) in new[] { (condition.All, " e ", 2), (condition.Any, " o ", 1) })
        {
            if (group is null)
            {
                continue;
            }

            var children = new List<(string Text, int Precedence)>();
            foreach (var child in group)
            {
                if (Format(child, out var childLevel) is not { } text)
                {
                    precedence = 0;
                    return null;
                }

                children.Add((text, childLevel));
            }

            if (children.Count == 1)
            {
                parts.Add(children[0]);
            }
            else if (children.Count > 1)
            {
                // "o" inside "e" needs brackets; "e" inside "o" gets them only to read better.
                parts.Add((string.Join(joiner, children.Select(c => c.Precedence < 3 && c.Precedence != level ? $"({c.Text})" : c.Text)), level));
            }
        }

        if (condition.Not is not null)
        {
            if (Format(condition.Not, out var notLevel) is not { } text)
            {
                precedence = 0;
                return null;
            }

            parts.Add(($"non {(notLevel < 3 ? $"({text})" : text)}", 3));
        }

        if (!string.IsNullOrWhiteSpace(condition.Expression))
        {
            parts.Add((condition.Expression.Trim(), NeedsBrackets(condition.Expression) ? 1 : 2));
        }

        if (parts.Count == 0)
        {
            precedence = 0;
            return null;
        }

        if (parts.Count == 1)
        {
            precedence = parts[0].Precedence;
            return parts[0].Text;
        }

        precedence = 2;
        return string.Join(" e ", parts.Select(p => p.Precedence < 2 ? $"({p.Text})" : p.Text));
    }

    private static string? FormatLeaf(RuleCondition leaf)
    {
        if (leaf.Field is null || !RuleFields.IsKnown(leaf.Field) || leaf.Op is null)
        {
            return null;
        }

        var name = RuleFields.ExpressionName(leaf.Field);
        var op = leaf.Op.ToLowerInvariant();
        if (op is "isnull" or "notnull")
        {
            return $"{name} {(op == "isnull" ? "=" : "!=")} vuoto";
        }

        if (op is "in" or "notin")
        {
            if (leaf.Value is not JsonArray array || array.Count == 0)
            {
                return null;
            }

            var items = array.Select(FormatValue).ToList();
            return items.Any(i => i is null) ? null : $"{name} {(op == "in" ? "tra" : "non tra")} ({string.Join(", ", items)})";
        }

        if (FormatValue(leaf.Value) is not { } value)
        {
            return null;
        }

        var symbol = op switch
        {
            "eq" => "=",
            "ne" => "!=",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            "contains" => "contiene",
            "startswith" => "iniziaCon",
            "regex" => "regex",
            _ => null,
        };

        return symbol is null ? null : $"{name} {symbol} {value}";
    }

    private static string? FormatValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String when value.GetValue<string>().Equals("$me", StringComparison.OrdinalIgnoreCase) => "$me",
            JsonValueKind.String => Quote(value.GetValue<string>()),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "sì",
            JsonValueKind.False => "no",
            _ => null,
        };
    }

    /// <summary>A text as an expression string: <c>"Supporto \"L1\""</c>.</summary>
    public static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>What the caret is in the middle of, so the editor can offer fields, operators or values.</summary>
    public static ExprCompletion CompletionAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var tokens = Tokenize(text[..caret]).ToList();
        var replaceStart = caret;

        // A word, number or string touching the caret is what is being typed: it gets replaced.
        if (tokens.Count > 0 && tokens[^1] is { } last && last.End == caret
            && (last.Kind is ExprTokenKind.Word or ExprTokenKind.Number or ExprTokenKind.Me || (last.Kind == ExprTokenKind.String && !last.IsClosed)))
        {
            replaceStart = last.Start;
            tokens.RemoveAt(tokens.Count - 1);
        }
        else if (tokens.Count > 0 && tokens[^1] is { Kind: ExprTokenKind.String, IsClosed: false } open)
        {
            replaceStart = open.Start;
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Count == 0)
        {
            return new ExprCompletion(ExprCompletionKind.Field, replaceStart);
        }

        var previous = tokens[^1];
        if (previous.Kind == ExprTokenKind.Symbol)
        {
            switch (previous.Text)
            {
                case "&&" or "||" or "!":
                    return new ExprCompletion(ExprCompletionKind.Field, replaceStart);
                case "(":
                    return IsListOperator(tokens, tokens.Count - 2) is { } listField
                        ? new ExprCompletion(ExprCompletionKind.Value, replaceStart, listField)
                        : new ExprCompletion(ExprCompletionKind.Field, replaceStart);
                case ",":
                    return OpenList(tokens) is { } open && IsListOperator(tokens, open - 1) is { } field
                        ? new ExprCompletion(ExprCompletionKind.Value, replaceStart, field)
                        : new ExprCompletion(ExprCompletionKind.None, replaceStart);
                case ")":
                    return new ExprCompletion(ExprCompletionKind.Logic, replaceStart);
                default: // a comparison
                    return new ExprCompletion(ExprCompletionKind.Value, replaceStart, FieldBefore(tokens, tokens.Count - 2));
            }
        }

        if (previous.Kind is ExprTokenKind.String or ExprTokenKind.Number or ExprTokenKind.Me)
        {
            return new ExprCompletion(ExprCompletionKind.Logic, replaceStart);
        }

        if (previous.Kind != ExprTokenKind.Word)
        {
            return new ExprCompletion(ExprCompletionKind.None, replaceStart);
        }

        switch (previous.Role)
        {
            case ExprWordRole.And or ExprWordRole.Or:
                return new ExprCompletion(ExprCompletionKind.Field, replaceStart);
            case ExprWordRole.Not:
                // "titolo non |" wants an operator; "e non |" wants a field.
                return tokens.Count >= 2 && tokens[^2] is { Kind: ExprTokenKind.Word, Role: ExprWordRole.None } field
                    ? new ExprCompletion(ExprCompletionKind.Operator, replaceStart, RuleFields.Resolve(field.Text), Negated: true)
                    : new ExprCompletion(ExprCompletionKind.Field, replaceStart);
            case ExprWordRole.Operator:
                return IsList(previous.Text)
                    ? new ExprCompletion(ExprCompletionKind.None, replaceStart)
                    : new ExprCompletion(ExprCompletionKind.Value, replaceStart, FieldBefore(tokens, tokens.Count - 2));
            case ExprWordRole.Literal:
                return new ExprCompletion(ExprCompletionKind.Logic, replaceStart);
            default:
                return new ExprCompletion(ExprCompletionKind.Operator, replaceStart, RuleFields.Resolve(previous.Text));
        }
    }

    private static bool IsList(string word) => word.Equals("tra", StringComparison.OrdinalIgnoreCase) || word.Equals("in", StringComparison.OrdinalIgnoreCase);

    /// <summary>The field of "campo [non] tra" when the token at <paramref name="index"/> is that "tra".</summary>
    private static string? IsListOperator(List<ExprToken> tokens, int index) =>
        index >= 0 && tokens[index] is { Kind: ExprTokenKind.Word, Role: ExprWordRole.Operator } op && IsList(op.Text)
            ? FieldBefore(tokens, index - 1) ?? ""
            : null;

    /// <summary>The field before an operator at <paramref name="index"/> + 1, skipping a "non".</summary>
    private static string? FieldBefore(List<ExprToken> tokens, int index)
    {
        if (index >= 0 && tokens[index].Role == ExprWordRole.Not)
        {
            index--;
        }

        return index >= 0 && tokens[index] is { Kind: ExprTokenKind.Word, Role: ExprWordRole.None } field ? RuleFields.Resolve(field.Text) : null;
    }

    /// <summary>The index of the bracket still open at the end of <paramref name="tokens"/>.</summary>
    private static int? OpenList(List<ExprToken> tokens)
    {
        var depth = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i] is { Kind: ExprTokenKind.Symbol, Text: ")" })
            {
                depth++;
            }
            else if (tokens[i] is { Kind: ExprTokenKind.Symbol, Text: "(" } && depth-- == 0)
            {
                return i;
            }
        }

        return null;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

    private sealed class ExprException(string message, int start, int length) : Exception(message)
    {
        public int Start { get; } = start;

        public int Length { get; } = length;
    }

    private sealed class Parser(IReadOnlyList<ExprToken> tokens, int textLength)
    {
        private int _index;

        public bool AtEnd => _index >= tokens.Count;

        public ExprToken? Current => AtEnd ? null : tokens[_index];

        public ExprNode ParseOr()
        {
            var left = ParseAnd();
            while (Is(ExprWordRole.Or, "||"))
            {
                _index++;
                left = new OrExpr(left, ParseAnd());
            }

            return left;
        }

        private ExprNode ParseAnd()
        {
            var left = ParseUnary();
            while (Is(ExprWordRole.And, "&&"))
            {
                _index++;
                left = new AndExpr(left, ParseUnary());
            }

            return left;
        }

        private ExprNode ParseUnary()
        {
            if (Is(ExprWordRole.Not, "!"))
            {
                _index++;
                return new NotExpr(ParseUnary());
            }

            if (IsSymbol("("))
            {
                var open = Current!;
                _index++;
                var inner = ParseOr();
                if (!IsSymbol(")"))
                {
                    throw AtEnd
                        ? new ExprException("manca la parentesi chiusa «)».", open.Start, open.Length)
                        : Unexpected("manca la parentesi chiusa «)»");
                }

                _index++;
                return inner;
            }

            return ParseComparison();
        }

        private ExprNode ParseComparison()
        {
            var token = Current ?? throw Incomplete("manca un campo");
            if (token.Kind != ExprTokenKind.Word || token.Role != ExprWordRole.None)
            {
                throw Unexpected("qui va un campo, es. priorita");
            }

            var field = RuleFields.Resolve(token.Text)
                ?? throw new ExprException($"campo sconosciuto «{token.Text}».", token.Start, token.Length);
            _index++;

            var negated = false;
            if (Current is { Role: ExprWordRole.Not } not)
            {
                negated = true;
                _index++;
                if (Current is not { Role: ExprWordRole.Operator })
                {
                    throw Current is null
                        ? Incomplete("dopo «non» va contiene, iniziaCon, regex o tra")
                        : Unexpected("dopo «non» va contiene, iniziaCon, regex o tra");
                }
            }

            var opToken = Current;
            if (!negated && (opToken is null || opToken.Role is ExprWordRole.And or ExprWordRole.Or || opToken is { Kind: ExprTokenKind.Symbol, Text: ")" or "&&" or "||" }))
            {
                // A yes/no field on its own: "in_sospeso" means "in_sospeso = sì".
                if (RuleFields.Find(field)?.Kind == RuleFieldKind.YesNo)
                {
                    return new TestExpr(field, "eq", ["true"]);
                }

                throw new ExprException($"dopo «{token.Text}» va un confronto, es. = \"valore\".", token.Start, token.Length);
            }

            if (opToken is null)
            {
                throw Incomplete("manca il confronto");
            }

            _index++;
            ExprNode test;
            if (opToken.Kind == ExprTokenKind.Symbol)
            {
                test = opToken.Text switch
                {
                    "=" or "==" => ValueOrEmpty(field, "eq", "isNull"),
                    "!=" or "<>" => ValueOrEmpty(field, "ne", "notNull"),
                    ">" => Number(field, "gt", opToken),
                    ">=" => Number(field, "gte", opToken),
                    "<" => Number(field, "lt", opToken),
                    "<=" => Number(field, "lte", opToken),
                    _ => throw new ExprException($"dopo «{token.Text}» va un confronto, es. = \"valore\".", opToken.Start, opToken.Length),
                };
            }
            else if (opToken.Role == ExprWordRole.Operator)
            {
                switch (opToken.Text.ToLowerInvariant())
                {
                    case "tra" or "in":
                        return new TestExpr(field, negated ? "notIn" : "in", List());
                    case "regex" or "corrisponde":
                        var pattern = Value(opToken, allowEmpty: false)!;
                        try
                        {
                            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
                        }
                        catch (ArgumentException ex)
                        {
                            var previous = tokens[_index - 1];
                            throw new ExprException($"espressione regolare non valida ({ex.Message}).", previous.Start, previous.Length);
                        }

                        test = new TestExpr(field, "regex", [pattern]);
                        break;
                    case "contiene" or "contains":
                        test = new TestExpr(field, "contains", [Value(opToken, allowEmpty: false)]);
                        break;
                    default:
                        test = new TestExpr(field, "startsWith", [Value(opToken, allowEmpty: false)]);
                        break;
                }
            }
            else
            {
                throw opToken.Kind == ExprTokenKind.Invalid
                    ? new ExprException($"carattere non ammesso «{opToken.Text}».", opToken.Start, opToken.Length)
                    : new ExprException($"dopo «{token.Text}» va un confronto (=, !=, >, <, contiene, tra...).", opToken.Start, opToken.Length);
            }

            return negated ? new NotExpr(test) : test;
        }

        private TestExpr ValueOrEmpty(string field, string op, string emptyOp)
        {
            var value = Value(tokens[_index - 1], allowEmpty: true);
            return value is null ? new TestExpr(field, emptyOp, []) : new TestExpr(field, op, [value]);
        }

        private TestExpr Number(string field, string op, ExprToken opToken)
        {
            var token = Current ?? throw Incomplete($"dopo «{opToken.Text}» va un numero");
            if (token.Kind != ExprTokenKind.Number || !double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                throw new ExprException($"«{opToken.Text}» confronta solo numeri.", token.Start, token.Length);
            }

            _index++;
            return new TestExpr(field, op, [token.Text]);
        }

        private List<string?> List()
        {
            var open = Current ?? throw Incomplete("dopo «tra» va un elenco tra parentesi, es. (1, 2)");
            if (!IsSymbol("("))
            {
                throw Unexpected("dopo «tra» va un elenco tra parentesi, es. (1, 2)");
            }

            _index++;
            var values = new List<string?>();
            while (true)
            {
                values.Add(Value(open, allowEmpty: false));
                if (IsSymbol(","))
                {
                    _index++;
                    continue;
                }

                if (IsSymbol(")"))
                {
                    _index++;
                    return values;
                }

                throw AtEnd ? new ExprException("manca la parentesi chiusa «)» dell'elenco.", open.Start, open.Length) : Unexpected("nell'elenco i valori si separano con la virgola");
            }
        }

        /// <summary>A value: text, number, yes/no, $me; null for "vuoto" (only where <paramref name="allowEmpty"/>).</summary>
        private string? Value(ExprToken after, bool allowEmpty)
        {
            const string expected = "qui va un valore: \"testo\", un numero, sì/no, vuoto o $me";
            var token = Current ?? throw Incomplete($"dopo «{after.Text}» va un valore");
            string? value;
            switch (token.Kind)
            {
                case ExprTokenKind.String when !token.IsClosed:
                    throw new ExprException("manca la virgoletta di chiusura.", token.Start, token.Length);
                case ExprTokenKind.String:
                    value = token.StringValue;
                    break;
                case ExprTokenKind.Number when double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                    value = token.Text;
                    break;
                case ExprTokenKind.Me:
                    value = "$me";
                    break;
                case ExprTokenKind.Word when token.Role == ExprWordRole.Literal:
                    var word = token.Text.ToLowerInvariant();
                    if (word is "vuoto" or "null")
                    {
                        if (!allowEmpty)
                        {
                            throw new ExprException("«vuoto» si usa solo con = e !=.", token.Start, token.Length);
                        }

                        value = null;
                    }
                    else
                    {
                        value = word is "sì" or "si" or "vero" or "true" ? "true" : "false";
                    }

                    break;
                case ExprTokenKind.Word when token.Role == ExprWordRole.None:
                    throw new ExprException($"{expected}. I testi vanno tra virgolette: \"{token.Text}\".", token.Start, token.Length);
                default:
                    throw new ExprException($"{expected}.", token.Start, token.Length);
            }

            _index++;
            return value;
        }

        private bool Is(ExprWordRole role, string symbol) =>
            Current is { } token && (token.Role == role || (token.Kind == ExprTokenKind.Symbol && token.Text == symbol));

        private bool IsSymbol(string symbol) => Current is { Kind: ExprTokenKind.Symbol } token && token.Text == symbol;

        private ExprException Incomplete(string what) => new($"l'espressione è incompleta: {what}.", textLength, 0);

        private ExprException Unexpected(string what)
        {
            var token = Current!;
            return token.Kind switch
            {
                ExprTokenKind.Invalid => new ExprException($"carattere non ammesso «{token.Text}».", token.Start, token.Length),
                ExprTokenKind.String when !token.IsClosed => new ExprException("manca la virgoletta di chiusura.", token.Start, token.Length),
                _ => new ExprException($"{what} (trovato «{token.Text}»).", token.Start, token.Length),
            };
        }
    }
}
