from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text()


def write(path: str, text: str) -> None:
    Path(path).write_text(text)


def replace_once(path: str, old: str, new: str = "") -> None:
    text = read(path)
    if old not in text:
        raise SystemExit(f"missing expected block in {path}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


def find_matching_brace(text: str, brace: int) -> int:
    depth = 0
    quote = None
    escaped = False
    index = brace
    while index < len(text):
        char = text[index]
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
        else:
            if char in ("'", '"'):
                quote = char
            elif char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
                if depth == 0:
                    return index + 1
        index += 1
    raise SystemExit("unbalanced brace")


def remove_decl(path: str, needle: str, include_attributes: bool = False) -> None:
    text = read(path)
    position = text.find(needle)
    if position < 0:
        raise SystemExit(f"missing declaration {needle!r} in {path}")
    start = text.rfind("\n", 0, position) + 1
    if include_attributes:
        while start > 0:
            previous_end = start - 1
            previous_start = text.rfind("\n", 0, previous_end) + 1
            previous = text[previous_start : previous_end + 1].strip()
            if previous.startswith("["):
                start = previous_start
                continue
            break
    brace = text.find("{", position)
    if brace < 0:
        raise SystemExit(f"no brace for {needle!r}")
    end = find_matching_brace(text, brace)
    while end < len(text) and text[end] in " \t\r\n":
        end += 1
    write(path, text[:start] + text[end:])


# No version-1 ownership marker existed on canary. Keep only the final payload.
remove_decl(
    "listenarr.infrastructure/Library/Moving/LibraryDirectoryOwnershipMarker.Payload.cs",
    "internal static bool MatchesLegacyPayload(",
)

# The marker reconciler/upgrade path exists only to upgrade older #717 marker formats.
for needle in (
    "internal static async Task ReconcileAsync(",
    "private static async Task ReconcileMarkerAsync(",
    "private static async Task UpgradeLegacyMarkerAsync(",
):
    remove_decl(
        "listenarr.infrastructure/Library/Moving/PinnedLibraryDirectoryOwnershipMarker.cs",
        needle,
    )

# Current relocation migration may retire either the source or target generation,
# but never a version-1 marker payload.
replace_once(
    "listenarr.infrastructure/Library/Moving/LibraryDirectoryOwnershipMarker.MigrationCleanup.cs",
    """        MatchesCurrentPayload(source, payload)
        || MatchesLegacyPayload(source, payload)
        || MatchesCurrentPayload(target, payload)
        || MatchesLegacyPayload(target, payload);""",
    """        MatchesCurrentPayload(source, payload)
        || MatchesCurrentPayload(target, payload);""",
)
replace_once(
    "listenarr.infrastructure/Library/Moving/PinnedLibraryDirectoryOwnershipMarker.Migration.cs",
    """        LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(source, payload)
        || LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(source, payload);""",
    """        LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(source, payload);""",
)

# Marker retirement accepts only the final persisted payload shape.
path = "listenarr.infrastructure/Library/Moving/LibraryDirectoryOwnershipMarker.cs"
text = read(path)
text = text.replace(
    """        if (!MatchesCurrentPayload(ownership, payload)
            && !MatchesLegacyPayload(ownership, payload))
        {
            throw new InvalidOperationException(
                "A legacy directory ownership artifact does not match the persisted ownership claim.");
        }
""",
    """        if (!MatchesCurrentPayload(ownership, payload))
        {
            throw new InvalidOperationException(
                "A directory ownership artifact does not match the persisted ownership claim.");
        }
""",
    1,
)
text = text.replace(
    """            throw new InvalidOperationException(
                "A legacy directory ownership artifact changed before retirement.");""",
    """            throw new InvalidOperationException(
                "A directory ownership artifact changed before retirement.");""",
)
text = text.replace(
    """        if (!MatchesCurrentPayload(ownership, verifiedPayload)
            && !MatchesLegacyPayload(ownership, verifiedPayload))
""",
    """        if (!MatchesCurrentPayload(ownership, verifiedPayload))
""",
    1,
)
write(path, text)

# Final ownership removal has no legacy quarantine or missing-both marker format.
path = "listenarr.infrastructure/Library/Moving/LibraryDirectoryOwnershipRemoval.cs"
remove_decl(path, "public static bool TryValidateLegacyMissingBothRecovery(")
text = read(path)
text = text.replace(
    """        var quarantinePath = GetQuarantinePath(ownership);
        var quarantineExists = Directory.Exists(quarantinePath);
        var quarantineIsFile = File.Exists(quarantinePath);
        if (originalIsFile || quarantineIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory recovery path is occupied by a file.");
        }
        if (originalExists && quarantineExists)
        {
            throw new InvalidOperationException(
                "Both the owned directory and its removal quarantine exist.");
        }

        if (!originalExists && !quarantineExists)
""",
    """        if (originalIsFile)
        {
            throw new InvalidOperationException(
                "The owned directory recovery path is occupied by a file.");
        }

        if (!originalExists)
""",
    1,
)
text = text.replace(
    """        var visiblePath = originalExists
            ? ownership.CanonicalPath
            : quarantinePath;
        var parentPath = Path.GetDirectoryName(visiblePath)
""",
    """        var parentPath = Path.GetDirectoryName(ownership.CanonicalPath)
""",
    1,
)
text = text.replace(
    """        using var directory = parent.OpenExistingChild(Path.GetFileName(visiblePath));""",
    """        using var directory = parent.OpenExistingChild(Path.GetFileName(ownership.CanonicalPath));""",
    1,
)
# Remove quarantine state from actual deletion path.
old_start = """        var quarantinePath = GetQuarantinePath(ownership);
        var originalExists = Directory.Exists(originalPath);
        var originalIsFile = File.Exists(originalPath);
        var quarantineExists = Directory.Exists(quarantinePath);
        var quarantineIsFile = File.Exists(quarantinePath);
        if (originalIsFile || quarantineIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory removal path is occupied by a file.");
        }
        if (originalExists && quarantineExists)
        {
            throw new InvalidOperationException(
                "Both the owned directory and its removal quarantine exist.");
        }
"""
new_start = """        var originalExists = Directory.Exists(originalPath);
        var originalIsFile = File.Exists(originalPath);
        if (originalIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory removal path is occupied by a file.");
        }
"""
if old_start not in text:
    raise SystemExit("missing removal quarantine preflight")
text = text.replace(old_start, new_start, 1)
text = text.replace(
    """        if (!originalExists && !quarantineExists)
        {
            RetireLegacySiblingArtifacts(ownership, parentAnchor);
            return LibraryDirectoryRemovalOutcome.AlreadyRemoved;
        }

        if (originalExists)
""",
    """        if (!originalExists)
        {
            RetireSiblingArtifacts(ownership, parentAnchor);
            return LibraryDirectoryRemovalOutcome.AlreadyRemoved;
        }

""",
    1,
)
# Keep only the original-path removal block; discard the quarantine compatibility block.
compat_marker = """        // Compatibility only: older versions may have already renamed the directory
        // into a job-shaped quarantine. New removals never create that pathname.
"""
compat_pos = text.find(compat_marker)
if compat_pos < 0:
    raise SystemExit("missing quarantine compatibility block")
# The original branch immediately before compatibility is closed with eight spaces + }.
# Preserve method close after deleting compatibility branch by replacing tail from marker through RestorePinnedQuarantine helper.
method_tail_start = compat_pos
restore_pos = text.find("    private static void RestorePinnedQuarantine(", compat_pos)
if restore_pos < 0:
    raise SystemExit("missing RestorePinnedQuarantine")
restore_brace = text.find("{", restore_pos)
restore_end = find_matching_brace(text, restore_brace)
while restore_end < len(text) and text[restore_end] in " \t\r\n":
    restore_end += 1
# Compatibility block occurs before helper methods; remove only it by finding the closing brace before RetireLegacyOwnershipArtifacts.
helpers_pos = text.find("    private static void RetireLegacyOwnershipArtifacts(", compat_pos)
if helpers_pos < 0 or helpers_pos > restore_pos:
    raise SystemExit("missing ownership artifact helpers")
text = text[:method_tail_start] + "    }\n\n" + text[helpers_pos:restore_pos] + text[restore_end:]
text = text.replace("RetireLegacyOwnershipArtifacts", "RetireOwnershipArtifacts")
text = text.replace("RetireLegacySiblingArtifacts", "RetireSiblingArtifacts")
text = text.replace("Legacy directory ownership artifacts", "Directory ownership artifacts")
text = text.replace("Legacy directory ownership sibling artifacts", "Directory ownership sibling artifacts")
write(path, text)

# Startup reconciliation supports only ownership rows produced by the final model.
path = "listenarr.infrastructure/Library/Moving/LibraryDirectoryOwnershipReconciler.cs"
replace_once(path, "        await BackfillLegacyRemovedOwnershipEvidenceAsync(db, cancellationToken);\n")
remove_decl(path, "private async Task BackfillLegacyRemovedOwnershipEvidenceAsync(")
text = read(path)
# Missing Removing path: no obsolete marker proof is needed; current deletion retires transient markers before namespace removal.
start = text.find("                    LibraryDirectoryOwnershipMarker.MarkerPayload? legacyPayload = null;")
if start < 0:
    raise SystemExit("missing legacy missing-removal reconciliation")
end_marker = "                    ownership.State = LibraryDirectoryOwnershipState.Removed;"
end = text.find(end_marker, start)
if end < 0:
    raise SystemExit("missing removed-state convergence")
text = text[:start] + text[end:]
# Require final physical identity; no null/v1 upgrade.
legacy_identity_start = text.find("                var liveIdentity = directory.GetDirectoryObjectIdentity();")
legacy_identity_end = text.find("                ownership.ManagedRootFolderId = authorization.RootFolderId;", legacy_identity_start)
if legacy_identity_start < 0 or legacy_identity_end < 0:
    raise SystemExit("missing identity reconciliation block")
replacement = """                var liveIdentity = directory.GetDirectoryObjectIdentity();
                if (ownership.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                    || !ManagedDirectoryIdentity.Matches(
                        ownership.DirectoryObjectIdentityVersion,
                        ownership.DirectoryObjectIdentity,
                        ownership.OwnershipToken,
                        liveIdentity))
                {
                    throw new InvalidOperationException(
                        "The persisted directory ownership identity is not the current supported generation.");
                }

"""
text = text[:legacy_identity_start] + replacement + text[legacy_identity_end:]
# Do not regenerate a different identity during reconciliation; validation above is authoritative.
text = text.replace(
    """                ownership.DirectoryObjectIdentityVersion =
                    ManagedDirectoryIdentity.CurrentVersion;
                ownership.DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                    ownership.OwnershipToken,
                    liveIdentity);
""",
    "",
    1,
)
text = text.replace(
    """                // Older builds left these marker files permanently. The durable row,
                // managed-root authorization, and pinned native directory generation
                // now provide the at-rest proof. Retire only artifacts that still match
                // this exact ownership; unrelated files are preserved.
""",
    """                // Root relocation may leave transient ownership migration artifacts
                // after a crash. Retire only artifacts that still match this exact final
                // ownership generation; unrelated files are preserved.
""",
    1,
)
text = text.replace("Obsolete directory ownership artifacts", "Directory ownership migration artifacts")
write(path, text)

# Retired marker evidence is final-format only.
path = "listenarr.infrastructure/Library/Moving/EfLibraryDirectoryOwnershipStore.State.cs"
remove_decl(path, "public static LibraryDirectoryOwnershipRetiredMarker CreateLegacyPending(")
text = read(path)
old = """        var payload = evidence.PayloadVersion == 1
            ? new LibraryDirectoryOwnershipMarker.MarkerPayload(
                1,
                evidence.OwnershipToken,
                evidence.CanonicalOwnershipPath)
            : new LibraryDirectoryOwnershipMarker.MarkerPayload(
                evidence.PayloadVersion,
                evidence.OwnershipToken,
                evidence.CanonicalOwnershipPath,
                evidence.OriginalManagedRootFolderId,
                evidence.DirectoryObjectIdentityVersion,
                evidence.DirectoryObjectIdentity);
"""
new = """        if (evidence.PayloadVersion != LibraryDirectoryOwnershipMarker.Version)
        {
            throw new InvalidOperationException(
                "The retired ownership marker evidence uses an unsupported payload version.");
        }
        var payload = new LibraryDirectoryOwnershipMarker.MarkerPayload(
            evidence.PayloadVersion,
            evidence.OwnershipToken,
            evidence.CanonicalOwnershipPath,
            evidence.OriginalManagedRootFolderId,
            evidence.DirectoryObjectIdentityVersion,
            evidence.DirectoryObjectIdentity);
"""
if old not in text:
    raise SystemExit("missing retired evidence legacy materialization")
write(path, text.replace(old, new, 1))

# Tests dedicated to intermediate ownership versions/formats are development history.
for test_file in (
    "tests/Features/Infrastructure/Library/Moving/EfLibraryDirectoryOwnershipStoreTests.cs",
    "tests/Features/Infrastructure/Library/Moving/LibraryDirectoryOwnershipReconcilerTests.cs",
    "tests/Features/Infrastructure/Library/Moving/LibraryDirectoryOwnershipMarkerTests.cs",
    "tests/Features/Infrastructure/Library/Moving/PinnedLibraryDirectoryOwnershipMarkerTests.cs",
):
    p = Path(test_file)
    if not p.exists():
        continue
    text = p.read_text()
    cursor = 0
    forbidden = (
        "MatchesLegacyPayload",
        "CreateLegacyPending",
        "TryValidateLegacyMissingBothRecovery",
        "legacy physical identity",
        "legacy marker",
        "LegacyMarker",
        "LegacyOwnership",
        "LegacyRemoved",
        "version one",
        "VersionOne",
    )
    while True:
        positions = [text.find(token, cursor) for token in forbidden]
        positions = [pos for pos in positions if pos >= 0]
        if not positions:
            break
        pos = min(positions)
        # Find containing test method attribute block.
        attr = max(text.rfind("    [Fact]", 0, pos), text.rfind("    [Theory]", 0, pos))
        if attr < 0:
            cursor = pos + 1
            continue
        method_brace = text.find("{", attr)
        if method_brace < 0 or method_brace > pos:
            cursor = pos + 1
            continue
        method_end = find_matching_brace(text, method_brace)
        if pos > method_end:
            cursor = pos + 1
            continue
        end = method_end
        while end < len(text) and text[end] in " \t\r\n":
            end += 1
        text = text[:attr] + text[end:]
        cursor = attr
    p.write_text(text)
