using System.Security.AccessControl;
using System.Security.Principal;

namespace LiveHelpDesk.Service;

/// <summary>
/// A profile holds a Dynamics token and the user's tickets: other users of the machine must not read it.
/// ProgramData lets every user read what is created in it by default, so the folder is closed explicitly.
/// </summary>
public static class ProfileSecurity
{
    /// <summary>Only SYSTEM, the administrators, the Windows service account and the account running the service may touch the folder.</summary>
    public static void EnsureProtected(string directory)
    {
        Directory.CreateDirectory(directory);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in PrincipalsWithAccess())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        try
        {
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
            // The folder already exists and belongs to another principal: the user-mode monitor created it
            // under the signed-in user and we are now the service (or the other way round). Only its owner or
            // an administrator can rewrite a protected ACL. It is already locked down, so leave it as it is
            // rather than failing the whole monitor startup over a permission we cannot change here.
        }
    }

    /// <summary>
    /// The principals a profile folder is opened to: the system and the administrators, the account that creates
    /// it, and always the Windows service account. The service runs that account and must reach a profile it
    /// manages even when the folder was first created by the user-mode monitor under the signed-in user.
    /// </summary>
    private static IEnumerable<SecurityIdentifier> PrincipalsWithAccess()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return WindowsIdentity.GetCurrent().User!;

        if (ServiceAccountSid() is { } service)
        {
            yield return service;
        }
    }

    /// <summary>The SID of <see cref="ServiceInstaller.ServiceAccount"/>; it is derived from the service name, so it resolves even before the service is installed.</summary>
    private static SecurityIdentifier? ServiceAccountSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(ServiceInstaller.ServiceAccount).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>The security identifiers that can reach <paramref name="directory"/>, for tests and diagnostics.</summary>
    public static IReadOnlyList<SecurityIdentifier> WhoCanAccess(string directory) =>
        new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .Distinct()
            .ToList();
}
