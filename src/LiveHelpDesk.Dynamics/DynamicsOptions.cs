namespace LiveHelpDesk.Dynamics;

/// <param name="Org">Organization URL, e.g. https://contoso.crm4.dynamics.com (no trailing slash).</param>
/// <param name="AppId">Model-driven app id used in browser deep links.</param>
/// <param name="TenantId">Entra tenant (GUID or domain).</param>
public sealed record DynamicsOptions(Uri Org, Guid AppId, string TenantId)
{
    public Uri ApiBase => new(Org, "/api/data/v9.2/");

    /// <summary>Scope for the Dataverse API (delegated "user_impersonation" via .default).</summary>
    public string Scope => $"{Org.GetLeftPart(UriPartial.Authority)}/.default";

    /// <summary>Link that opens a case in the model-driven app (used by the embedded WebView).</summary>
    public Uri IncidentUri(Guid incidentId) =>
        new(Org, $"/main.aspx?appid={AppId:D}&pagetype=entityrecord&etn=incident&id={incidentId:D}");
}
