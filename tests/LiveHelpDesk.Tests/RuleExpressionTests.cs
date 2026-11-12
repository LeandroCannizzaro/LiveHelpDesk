using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.Views;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Rules;
using Xunit;

namespace LiveHelpDesk.Tests;

public class RuleExpressionTests
{
    private static readonly Guid Me = T.Id(900);
    private static readonly Guid Queue = T.Id(500);
    private static readonly EvaluationContext Context = new(T.At(120), TicketMapping.Qgs, Me);

    private static TicketEvent Event(TicketSnapshot ticket, MessageSnapshot? message = null, TicketEventType type = TicketEventType.TicketCreated) =>
        new("k", type, T.At(0), ticket.Id, ticket.Number, ticket.Title, Ticket: ticket, Message: message);

    private static TicketSnapshot Urgent() =>
        T.Ticket(priority: 1, queue: Queue, owner: Me, title: "Fatture bloccate", extra: new Dictionary<string, string?> { ["applicativo"] = "100000001" }) with
        {
            Labels = new Dictionary<string, string?>
            {
                [TicketLabels.Queue] = "Supporto L1",
                [TicketLabels.Customer] = "Rossi Forniture S.r.l.",
                [TicketLabels.Owner] = "Luca Bianchi",
                ["applicativo"] = "Fatturazione elettronica",
            },
        };

    private static bool Eval(string expression, TicketEvent evt)
    {
        var parsed = RuleExpression.Parse(expression);
        Assert.True(parsed.IsValid, parsed.Error?.Describe(expression));
        return RuleExpression.Evaluate(expression, evt, Context);
    }

    [Theory]
    [InlineData("priorita = 1", true)]
    [InlineData("priorita = \"Alta\"", true)]
    [InlineData("priorita = \"alta\"", true)]
    [InlineData("priorita != \"Alta\"", false)]
    [InlineData("priorita tra (\"Normale\", 1)", true)]
    [InlineData("priorita non tra (\"Alta\", \"Normale\")", false)]
    [InlineData("coda = \"Supporto L1\"", true)]
    [InlineData("coda = \"000001f4-0000-0000-0000-000000000000\"", true)]
    [InlineData("proprietario = $me", true)]
    [InlineData("utente_l2 = vuoto", true)]
    [InlineData("utente_l2 != vuoto", false)]
    [InlineData("cliente contiene \"rossi\"", true)]
    [InlineData("titolo iniziaCon \"fatture\"", true)]
    [InlineData("titolo non contiene \"fattur\"", false)]
    [InlineData("titolo regex \"^Fat+ure\"", true)]
    [InlineData("applicativo = \"Fatturazione elettronica\"", true)]
    [InlineData("applicativo = \"100000001\"", true)]
    [InlineData("livello = \"L1\"", true)]
    [InlineData("stato_ticket = \"Attivo\"", true)]
    [InlineData("stato = \"L1 Assegnazione\"", true)]
    [InlineData("eta_minuti > 60", true)]
    [InlineData("eta_minuti <= 60", false)]
    [InlineData("in_sospeso", false)]
    [InlineData("non in_sospeso", true)]
    [InlineData("in_sospeso = no e sollecito = falso", true)]
    [InlineData("tipo_evento = \"TicketCreated\"", true)]
    public void Comparisons_WorkByCodeOrByName(string expression, bool expected)
    {
        Assert.Equal(expected, Eval(expression, Event(Urgent())));
    }

    [Theory]
    [InlineData("priorita = \"Bassa\" o coda = \"Supporto L1\" e titolo contiene \"x\"", false)] // "e" binds tighter than "o"
    [InlineData("(priorita = \"Bassa\" o coda = \"Supporto L1\") e titolo contiene \"fatture\"", true)]
    [InlineData("non (priorita = \"Alta\" e in_sospeso)", true)]
    [InlineData("priorita = \"Alta\" AND (coda = \"Urgenze\" OR NOT sollecito)", true)]
    [InlineData("priorita = 'Alta' && !(coda = \"Urgenze\" || sollecito)", true)]
    [InlineData("mittente contiene \"@example.com\" e messaggio_ricevuto e tipo_messaggio = \"E-mail\"", true)]
    public void Logic_FollowsTheUsualPrecedence_InItalianOrEnglish(string expression, bool expected)
    {
        Assert.Equal(expected, Eval(expression, Event(Urgent(), T.Message(1, 1))));
    }

    [Theory]
    [InlineData("", "vuota", 0)]
    [InlineData("priorita = ", "incompleta", 11)]
    [InlineData("pippo = 1", "campo sconosciuto «pippo»", 0)]
    [InlineData("titolo contiene fattura", "virgolette", 16)]
    [InlineData("titolo contiene \"fattura", "virgoletta di chiusura", 16)]
    [InlineData("eta_minuti > \"tanti\"", "solo numeri", 13)]
    [InlineData("(priorita = 1", "parentesi chiusa", 0)]
    [InlineData("priorita = 1)", "parentesi chiusa senza", 12)]
    [InlineData("priorita = 1 coda = 2", "manca «e» oppure «o»", 13)]
    [InlineData("titolo regex \"[\"", "espressione regolare", 13)]
    [InlineData("titolo = vuoto o titolo contiene vuoto", "solo con = e !=", 33)]
    [InlineData("coda", "va un confronto", 0)]
    [InlineData("priorita # 1", "carattere non ammesso", 9)]
    [InlineData("priorita tra 1, 2", "elenco tra parentesi", 13)]
    public void Mistakes_AreFound_WithWhereTheyAre(string expression, string message, int start)
    {
        var error = RuleExpression.Parse(expression).Error;

        Assert.NotNull(error);
        Assert.Contains(message, error.Message);
        Assert.Equal(start, error.Start);
    }

    [Fact]
    public void TheExpression_BecomesALinqExpressionTree()
    {
        var linq = RuleExpression.Parse("priorita = \"Alta\" e non (coda = \"Urgenze\" o sollecito)").ToLinq();

        Assert.NotNull(linq);
        Assert.Equal(System.Linq.Expressions.ExpressionType.AndAlso, linq.Body.NodeType);
        var tickets = new[] { Urgent(), T.Ticket(priority: 3), T.Ticket(priority: 1, reminder: true) };
        var matching = tickets.Select(t => Event(t)).Where(e => linq.Compile()(e, Context)).ToList();
        Assert.Single(matching);
    }

    [Fact]
    public void TheRuleEngine_PutsTheExpressionInAndWithTheOtherConditions()
    {
        var result = RulesConfig.Parse(
            """
            { "version": 1, "rules": [ { "id": "r", "events": ["TicketCreated"], "actions": { "channels": ["toast"] },
              "when": { "all": [ { "field": "priority", "op": "eq", "value": 1 }, { "expression": "coda = \"Supporto L1\" o sollecito" } ] } } ] }
            """);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var rule = result.File!.Rules[0];

        Assert.Single(RuleEngine.Evaluate(Event(Urgent()), [rule], Context));
        Assert.Empty(RuleEngine.Evaluate(Event(Urgent() with { Priority = 2 }), [rule], Context));
        Assert.Empty(RuleEngine.Evaluate(Event(Urgent() with { Labels = new Dictionary<string, string?>(), QueueId = null }), [rule], Context));
    }

    [Fact]
    public void AnInvalidExpression_IsRefusedByTheRulesFile_WithItsPosition()
    {
        var result = RulesConfig.Parse(
            """{ "version": 1, "rules": [ { "id": "r", "events": ["TicketCreated"], "actions": { "channels": ["toast"] }, "when": { "expression": "priorita = \"Alta\" e pippo = 1" } } ] }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("regola 'r'") && e.Contains("colonna 21") && e.Contains("pippo"));
    }

    [Fact]
    public void FormattedConditions_ReadBackToTheSameMeaning()
    {
        var condition = new RuleCondition
        {
            Any =
            [
                new RuleCondition { Field = "title", Op = "regex", Value = System.Text.Json.Nodes.JsonValue.Create("^\\d+ \"x\"") },
                new RuleCondition { All = [new RuleCondition { Field = "priority", Op = "in", Value = new System.Text.Json.Nodes.JsonArray(1, 2) }, new RuleCondition { Field = "queue", Op = "isNull" }] },
            ],
            Not = new RuleCondition { Field = "onHold", Op = "eq", Value = System.Text.Json.Nodes.JsonValue.Create(true) },
        };

        var text = RuleExpression.Format(condition);

        Assert.Equal("""(titolo regex "^\\d+ \"x\"" o (priorita tra (1, 2) e coda = vuoto)) e non in_sospeso = sì""", text);
        foreach (var ticket in new[] { Urgent(), T.Ticket(priority: 2), T.Ticket(priority: 2, onHold: true), T.Ticket(title: "12 \"x\"") })
        {
            Assert.Equal(RuleEngine.Matches(condition, Event(ticket), Context), RuleExpression.Evaluate(text!, Event(ticket), Context));
        }
    }

    [Theory]
    [InlineData("", ExprCompletionKind.Field, 0, null)]
    [InlineData("pri", ExprCompletionKind.Field, 0, null)]
    [InlineData("priorita ", ExprCompletionKind.Operator, 9, "priority")]
    [InlineData("titolo non ", ExprCompletionKind.Operator, 11, "title")]
    [InlineData("priorita = ", ExprCompletionKind.Value, 11, "priority")]
    [InlineData("priorita = \"Al", ExprCompletionKind.Value, 11, "priority")]
    [InlineData("coda tra (\"A\", ", ExprCompletionKind.Value, 15, "queue")]
    [InlineData("coda non tra (", ExprCompletionKind.Value, 14, "queue")]
    [InlineData("priorita = 1 ", ExprCompletionKind.Logic, 13, null)]
    [InlineData("priorita = 1 e (co", ExprCompletionKind.Field, 16, null)]
    [InlineData("priorita = 1 o non ", ExprCompletionKind.Field, 19, null)]
    public void TheEditor_KnowsWhatToSuggest_AtTheCaret(string text, ExprCompletionKind kind, int replaceStart, string? field)
    {
        var completion = RuleExpression.CompletionAt(text, text.Length);

        Assert.Equal(kind, completion.Kind);
        Assert.Equal(replaceStart, completion.ReplaceStart);
        Assert.Equal(field, completion.Field);
    }

    [Fact]
    public void SuggestedValues_AreWrittenByName_WhenTheFieldCanBeComparedByName()
    {
        var fields = RuleCatalog.Fields(TicketMapping.Qgs, [new LiveHelpDesk.Ipc.LookupItem(Queue.ToString("D"), "Supporto L1", "queue")]);
        FieldInfo Field(string key) => fields.First(f => f.Key == key);

        Assert.Equal("\"Alta\"", RuleExpressionEditor.Literal(Field("priority"), Field("priority").Choices[0]));
        Assert.Equal("\"Supporto L1\"", RuleExpressionEditor.Literal(Field("queue"), Field("queue").Choices[0]));
        Assert.Equal("$me", RuleExpressionEditor.Literal(Field("owner"), Field("owner").Choices[0]));
        Assert.Equal("sì", RuleExpressionEditor.Literal(Field("onHold"), Field("onHold").Choices[0]));
        Assert.Equal("\"TicketCreated\"", RuleExpressionEditor.Literal(Field("eventType"), Field("eventType").Choices[0]));

        // Each suggested literal is accepted by the parser and matches a ticket that has that value.
        var literal = RuleExpressionEditor.Literal(Field("queue"), Field("queue").Choices[0]);
        Assert.True(Eval($"coda = {literal}", Event(Urgent())));
    }
}
