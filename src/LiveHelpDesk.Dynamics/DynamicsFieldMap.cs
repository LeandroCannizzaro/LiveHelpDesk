namespace LiveHelpDesk.Dynamics;

/// <summary>
/// Which Dataverse columns carry which meaning in this installation. The defaults are the QGS help desk;
/// they will be loaded from <c>dynamics-mapping.json</c> so nothing here is hard-coded in the pipeline.
/// </summary>
public sealed record DynamicsFieldMap
{
    public static DynamicsFieldMap Qgs { get; } = new();

    /// <summary>Lookup columns are read as <c>_name_value</c>.</summary>
    public string Queue { get; init; } = "gs_codateam";

    public string Level2User { get; init; } = "gs_utentediliv2";

    public string Customer { get; init; } = "gs_cliente";

    public string CommunicationStatus { get; init; } = "gs_statocomunicazioni";

    public string Reminder { get; init; } = "gs_sollecito";

    public string OnHold { get; init; } = "gs_insospeso";

    /// <summary>The picklist value that means "yes" for <see cref="Reminder"/> and <see cref="OnHold"/>.</summary>
    public int YesValue { get; init; } = 717600000;

    /// <summary>Extra columns exposed to rules as <c>extra.&lt;role&gt;</c> (raw values).</summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } = new Dictionary<string, string>
    {
        ["applicativo"] = "gs_applicativo",
        ["tipologia"] = "cr754_tipologiaticket",
        ["businessunit"] = "cr754_businessunit",
        ["classificazione"] = "gs_classificazione",
        ["prioritariaqualificata"] = "gs_prioritariqualificata",
    };

    /// <summary>The Dataverse column names to ask for when reading tickets.</summary>
    public IEnumerable<string> TicketColumns =>
        new[]
        {
            "incidentid", "ticketnumber", "title", "statecode", "statuscode", "prioritycode", "createdon", "modifiedon",
            "_ownerid_value", Lookup(Queue), Lookup(Level2User), Lookup(Customer), CommunicationStatus, Reminder, OnHold,
            "_createdby_value", "_modifiedby_value",
        }.Concat(Extras.Values).Distinct();

    public static string Lookup(string name) => $"_{name}_value";
}
