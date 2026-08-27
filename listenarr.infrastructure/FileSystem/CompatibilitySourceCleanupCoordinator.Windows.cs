using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class CompatibilitySourceCleanupCoordinator
{
    [SupportedOSPlatform("windows")]
    private static void ApplyPrivateWindowsAcl(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException(
                "The current Windows identity has no security identifier.");
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.SetAccessControl(security);

        var applied = directory.GetAccessControl(AccessControlSections.Access);
        if (!applied.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException(
                "The quarantine directory still inherits Windows access rules.");
        }

        var unexpectedAllow = applied.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier identifier
                && !identifier.Equals(currentUser));
        if (unexpectedAllow)
        {
            throw new UnauthorizedAccessException(
                "The quarantine directory permissions are not private.");
        }
    }
}
