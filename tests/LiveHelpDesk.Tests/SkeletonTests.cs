using LiveHelpDesk.Core;
using LiveHelpDesk.Dynamics;
using Xunit;

namespace LiveHelpDesk.Tests;

public class DedupKeysTests
{
    private static readonly Guid Ticket = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Created_IsStableForTheSameTicket() =>
        Assert.Equal(DedupKeys.Created(Ticket), DedupKeys.Created(Ticket));

    [Fact]
    public void StatusChanged_NormalizesTimeZone()
    {
        var utc = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
        var local = new DateTimeOffset(2026, 10, 30, 16, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal(
            DedupKeys.StatusChanged(Ticket, 1, 717600002, utc),
            DedupKeys.StatusChanged(Ticket, 1, 717600002, local));
    }

    [Fact]
    public void StatusChanged_DiffersWhenTransitionDiffers()
    {
        var at = DateTimeOffset.UnixEpoch;

        Assert.NotEqual(
            DedupKeys.StatusChanged(Ticket, 1, 2, at),
            DedupKeys.StatusChanged(Ticket, 1, 3, at));
    }
}

public class DynamicsOptionsTests
{
    private static readonly DynamicsOptions Options = new(
        new Uri("https://example.crm4.dynamics.com"),
        Guid.Parse("d2ed1394-6c4e-ed11-bba3-00224899337b"),
        "tenant");

    [Fact]
    public void IncidentUri_PointsToTheCaseInTheApp()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(
            "https://example.crm4.dynamics.com/main.aspx?appid=d2ed1394-6c4e-ed11-bba3-00224899337b&pagetype=entityrecord&etn=incident&id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            Options.IncidentUri(id).ToString());
    }

    [Fact]
    public void Scope_UsesTheOrgAuthority() =>
        Assert.Equal("https://example.crm4.dynamics.com/.default", Options.Scope);

    [Fact]
    public void ApiBase_IsTheWebApiRoot() =>
        Assert.Equal("https://example.crm4.dynamics.com/api/data/v9.2/", Options.ApiBase.ToString());
}

public class AppPathsTests
{
    [Fact]
    public void ProfileDirectory_IsPerSidUnderMachineRoot() =>
        Assert.Equal(Path.Combine(AppPaths.MachineRoot, "profiles", "S-1-5-21-1"), AppPaths.ProfileDirectory("S-1-5-21-1"));
}
