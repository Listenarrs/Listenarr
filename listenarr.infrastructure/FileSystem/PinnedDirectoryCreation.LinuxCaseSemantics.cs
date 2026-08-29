using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private const int LinuxAtSymlinkNoFollow = 0x0100;
    private const uint LinuxStatxType = 0x00000001;
    private const uint LinuxStatxLinkCount = 0x00000004;
    private const uint LinuxStatxInode = 0x00000100;
    private const uint LinuxStatxMountId = 0x00001000;
    private const ushort LinuxFileTypeMask = 0xf000;
    private const ushort LinuxDirectoryType = 0x4000;
    private const ushort LinuxRegularFileType = 0x8000;
    private const ushort LinuxSymbolicLinkType = 0xa000;

    internal enum LinuxCaseAliasProbeOutcome
    {
        Sensitive,
        Insensitive,
        RetryCandidate,
        Unavailable
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal LinuxCaseAliasProbeOutcome ProbeLinuxCaseAlias(
            string exactName,
            string alternateName,
            out string? reason)
        {
            ThrowIfDisposed();
            ValidateLeafName(exactName);
            ValidateLeafName(alternateName);
            reason = null;
            if (!OperatingSystem.IsLinux())
            {
                reason = "Linux case-alias probing is available only on Linux.";
                return LinuxCaseAliasProbeOutcome.Unavailable;
            }
            if (string.Equals(exactName, alternateName, StringComparison.Ordinal))
            {
                reason = "The candidate name has no distinct case variant.";
                return LinuxCaseAliasProbeOutcome.RetryCandidate;
            }
            if (!VisiblePathMatches())
            {
                reason = "The filesystem boundary changed before case-sensitivity probing.";
                return LinuxCaseAliasProbeOutcome.Unavailable;
            }

            var exactBefore = TryReadLinuxCaseEntryIdentity(exactName, out var exactError);
            if (exactBefore == null)
            {
                reason = exactError;
                return LinuxCaseAliasProbeOutcome.RetryCandidate;
            }
            if (exactBefore.Value.IsSymbolicLink)
            {
                reason = "Symbolic-link entries are not used to infer filesystem case sensitivity.";
                return LinuxCaseAliasProbeOutcome.RetryCandidate;
            }
            if (!exactBefore.Value.IsDirectory && !exactBefore.Value.IsRegularFile)
            {
                reason = "Only regular files and directories are used to infer filesystem case sensitivity.";
                return LinuxCaseAliasProbeOutcome.RetryCandidate;
            }

            var alternate = TryReadLinuxCaseEntryIdentity(alternateName, out var alternateError);
            var exactAfter = TryReadLinuxCaseEntryIdentity(exactName, out var exactAfterError);
            if (exactAfter == null || !exactBefore.Value.Equals(exactAfter.Value))
            {
                reason = exactAfterError
                    ?? "The case-probe entry changed while filesystem semantics were being observed.";
                return LinuxCaseAliasProbeOutcome.RetryCandidate;
            }
            if (!VisiblePathMatches())
            {
                reason = "The filesystem boundary changed while case sensitivity was being observed.";
                return LinuxCaseAliasProbeOutcome.Unavailable;
            }

            if (alternate == null)
            {
                if (alternateError == null)
                {
                    return LinuxCaseAliasProbeOutcome.Sensitive;
                }

                reason = alternateError;
                return LinuxCaseAliasProbeOutcome.RetryCandidate;
            }

            if (!exactBefore.Value.SameObjectPathIdentityAs(alternate.Value))
            {
                return LinuxCaseAliasProbeOutcome.Sensitive;
            }
            return ClassifySameLinuxCaseAlias(
                exactBefore.Value.AliasEvidence,
                alternate.Value.AliasEvidence,
                out reason);
        }

        private LinuxCaseEntryIdentity? TryReadLinuxCaseEntryIdentity(
            string childName,
            out string? error)
        {
            error = null;
            if (Statx(
                    _handle.DangerousGetHandle().ToInt32(),
                    childName,
                    LinuxAtSymlinkNoFollow,
                    LinuxStatxType | LinuxStatxLinkCount | LinuxStatxInode | LinuxStatxMountId,
                    out var information) == 0)
            {
                if ((information.Mask & LinuxStatxInode) == 0)
                {
                    error = "The filesystem did not expose an inode for the case-probe entry.";
                    return null;
                }

                return new LinuxCaseEntryIdentity(
                    information.DeviceMajor,
                    information.DeviceMinor,
                    information.Inode,
                    information.Mode,
                    information.LinkCount,
                    (information.Mask & LinuxStatxLinkCount) != 0,
                    information.MountId,
                    (information.Mask & LinuxStatxMountId) != 0);
            }

            var nativeError = Marshal.GetLastWin32Error();
            if (nativeError == UnixNoEntry)
            {
                return null;
            }

            error = new Win32Exception(nativeError).Message;
            return null;
        }
    }

    internal static LinuxCaseAliasProbeOutcome ClassifySameLinuxCaseAlias(
        LinuxCaseAliasEvidence exact,
        LinuxCaseAliasEvidence alternate,
        out string? reason)
    {
        reason = null;
        if (!exact.HasMountId || !alternate.HasMountId)
        {
            reason = "Case-variant names expose the same device/inode but mount identity is unavailable, so aliasing cannot be excluded.";
            return LinuxCaseAliasProbeOutcome.RetryCandidate;
        }
        if (exact.MountId != alternate.MountId)
        {
            return LinuxCaseAliasProbeOutcome.Sensitive;
        }
        if (exact.IsDirectory)
        {
            return LinuxCaseAliasProbeOutcome.Insensitive;
        }
        if (exact.IsRegularFile
            && alternate.IsRegularFile
            && exact.HasLinkCount
            && alternate.HasLinkCount
            && exact.LinkCount == 1
            && alternate.LinkCount == 1)
        {
            return LinuxCaseAliasProbeOutcome.Insensitive;
        }

        reason = "Case-variant names resolve to the same multiply-linked or otherwise alias-ambiguous regular file, so lookup semantics are ambiguous.";
        return LinuxCaseAliasProbeOutcome.RetryCandidate;
    }

    internal readonly record struct LinuxCaseAliasEvidence(
        bool IsDirectory,
        bool IsRegularFile,
        uint LinkCount,
        bool HasLinkCount,
        ulong MountId,
        bool HasMountId);

    private readonly record struct LinuxCaseEntryIdentity(
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Inode,
        ushort Mode,
        uint LinkCount,
        bool HasLinkCount,
        ulong MountId,
        bool HasMountId)
    {
        internal bool IsDirectory =>
            (Mode & LinuxFileTypeMask) == LinuxDirectoryType;

        internal bool IsRegularFile =>
            (Mode & LinuxFileTypeMask) == LinuxRegularFileType;

        internal bool IsSymbolicLink =>
            (Mode & LinuxFileTypeMask) == LinuxSymbolicLinkType;

        internal LinuxCaseAliasEvidence AliasEvidence => new(
            IsDirectory,
            IsRegularFile,
            LinkCount,
            HasLinkCount,
            MountId,
            HasMountId);

        internal bool SameObjectPathIdentityAs(LinuxCaseEntryIdentity other) =>
            DeviceMajor == other.DeviceMajor
            && DeviceMinor == other.DeviceMinor
            && Inode == other.Inode;
    }
}
