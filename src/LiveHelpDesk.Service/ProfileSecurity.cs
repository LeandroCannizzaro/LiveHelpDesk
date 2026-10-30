using System.Security.AccessControl;
using System.Security.Principal;

namespace LiveHelpDesk.Service;

/// <summary>
/// A profile holds a Dynamics token and the user's tickets: other users of the machine must not read it.
/// ProgramData lets every user read what is created in it by default, so the folder is closed explicitly.
/// </summary>
public static class ProfileSecurity
{
    /// <summary>Only SYSTEM, the administrators and the account running the service may touch the folder.</summary>
    public static void EnsureProtected(string directory)
    {
        Directory.CreateDirectory(directory);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
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
