from pathlib import Path
import re


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


def remove_false_marker_assertions(path: str) -> None:
    text = read(path)
    pattern = re.compile(
        r"\n\s*Assert\.False\(File\.Exists\(Path\.Join\(\s*[^,\n]+,\s*ManagedDirectoryEnrollment\.FileName\s*\)\)\);",
        re.MULTILINE,
    )
    write(path, pattern.sub("", text))


# Production: remove the reader/retirer and every fallback that depends on it.
legacy_file = Path("listenarr.infrastructure/FileSystem/ManagedDirectoryEnrollment.cs")
if not legacy_file.exists():
    raise SystemExit("missing ManagedDirectoryEnrollment.cs")
legacy_file.unlink()

replace_once(
    "listenarr.infrastructure/Library/Moving/AudiobookContentMoveService.SourceValidation.cs",
    """                    if (string.Equals(
                            entryName,
                            ManagedDirectoryEnrollment.FileName,
                            StringComparison.Ordinal)
                        && IsSourceCleanupBoundary(
                            source,
                            persistentManagedRootBoundary,
                            sourceSemantics)
                        && FileSystemPathIdentity.AreEquivalent(
                            Path.GetDirectoryName(entry)!,
                            source,
                            sourceSemantics))
                    {
                        var enrollmentAttributes = File.GetAttributes(entry);
                        if ((enrollmentAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        {
                            throw new MoveNeedsAttentionException(
                                "The managed-root enrollment artifact changed type or became linked.");
                        }

                        // The root enrollment belongs to the persistent cleanup boundary,
                        // not to the audiobook. Leave it in place and exclude it from the
                        // move manifest/companion sweep.
                        continue;
                    }

""",
)
replace_once(
    "listenarr.infrastructure/Library/Moving/EfMoveExecutionStore.Helpers.cs",
    """            if (!string.Equals(
                    currentDigest,
                    expectedDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Last-resort compatibility for jobs created before a configured-root
                // identity was available in the database. Read an existing legacy
                // marker only; never create one.
                var legacy = ManagedDirectoryEnrollment.ResolveExisting(
                    boundary,
                    nativeIdentity);
                currentDigest = legacy.IsAvailable && legacy.Version == currentVersion
                    ? MoveManifestIdentity.ComputeTargetBoundaryAuthorizationDigest(
                        currentVersion,
                        legacy.Value!)
                    : currentDigest;
            }

""",
)
replace_once(
    "listenarr.infrastructure/Library/Moving/MoveFilesystemArtifactNames.cs",
    "        || string.Equals(name, ManagedDirectoryEnrollment.FileName, StringComparison.Ordinal)\n",
)
replace_once(
    "listenarr.infrastructure/Library/Moving/RootFolderRelocationService.TargetReservationPersistence.cs",
    "        ManagedDirectoryEnrollment.RetireValidMarker(directory);\n",
)
replace_once(
    "listenarr.infrastructure/Persistence/RootFolderObjectIdentityReconciler.cs",
    """            TryRetireLegacyRootEnrollmentMarker(
                canonicalRootPath,
                root,
                logger);
""",
)
remove_decl(
    "listenarr.infrastructure/Persistence/RootFolderObjectIdentityReconciler.cs",
    "private static void TryRetireLegacyRootEnrollmentMarker(",
)

# Root identity version 1 existed only in intermediate #717 builds.
replace_once(
    "listenarr.application/Audiobooks/Contracts/IDirectoryObjectIdentityResolver.cs",
    """    Task<DirectoryObjectIdentityResolution> UpgradeLegacyAsync(
        string path,
        int legacyVersion,
        string legacyValue,
        CancellationToken cancellationToken = default);
""",
)
remove_decl(
    "listenarr.infrastructure/FileSystem/DirectoryObjectIdentityResolver.cs",
    "public Task<DirectoryObjectIdentityResolution> UpgradeLegacyAsync(",
)
replace_once(
    "listenarr.infrastructure/Persistence/RootFolderObjectIdentityReconciler.cs",
    """            else if (root.DirectoryObjectIdentityVersion == 1)
            {
                current = await identityResolver.UpgradeLegacyAsync(
                    canonicalRootPath,
                    root.DirectoryObjectIdentityVersion.Value,
                    root.DirectoryObjectIdentity,
                    cancellationToken);
            }
""",
)

# Architecture gate becomes a negative production-surface rule.
replace_once(
    "tests/Features/Architecture/BackendArchitectureTests.cs",
    """    [Fact]
    public void RootDirectoryIdentity_DoesNotPublishPermanentFilesystemEnrollment()
    {
        var legacyEnrollmentSource = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "FileSystem",
            "ManagedDirectoryEnrollment.cs"));
        var resolverSource = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "FileSystem",
            "DirectoryObjectIdentityResolver.cs"));

        Assert.DoesNotContain(
            "PublishNewFileAsync",
            legacyEnrollmentSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "enrollIfMissing",
            legacyEnrollmentSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ManagedDirectoryEnrollment",
            resolverSource,
            StringComparison.Ordinal);
    }

""",
    """    [Fact]
    public void RootDirectoryIdentity_HasNoIntermediateFilesystemEnrollmentCompatibility()
    {
        Assert.False(File.Exists(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "FileSystem",
            "ManagedDirectoryEnrollment.cs")));

        var productionRoots = new[]
        {
            Path.Join(RepositoryRoot, "listenarr.application"),
            Path.Join(RepositoryRoot, "listenarr.domain"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure"),
            Path.Join(RepositoryRoot, "listenarr.api")
        };
        var violations = productionRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("ManagedDirectoryEnrollment", StringComparison.Ordinal)
                    || source.Contains(".listenarr-root-enrollment.json", StringComparison.Ordinal)
                    || source.Contains("UpgradeLegacyAsync", StringComparison.Ordinal);
            })
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

""",
)

# Tests dedicated to the discarded marker/version transition are removed.
remove_decl(
    "tests/Features/Application/Audiobooks/RootFolders/RootFolderServiceTests.cs",
    "public async Task ReauthorizeDirectoryIdentity_InvalidLegacyMarker_IsIgnoredAndPreserved()",
    include_attributes=True,
)
remove_decl(
    "tests/Features/Application/Audiobooks/RootFolders/RootFolderServiceTests.cs",
    "public async Task Create_NestedRejectedRoot_DoesNotEnrollCandidateDirectory()",
    include_attributes=True,
)
remove_decl(
    "tests/Features/Infrastructure/FileSystem/DirectoryObjectIdentityResolverTests.cs",
    "public async Task ResolveExistingAsync_ForeignMarkerCannotAuthorizeDifferentNativeGeneration()",
    include_attributes=True,
)
remove_decl(
    "tests/Features/Infrastructure/FileSystem/DirectoryObjectIdentityResolverTests.cs",
    "public async Task UpgradeLegacyAsync_MatchingNativeIdentity_ProducesMarkerlessVersionTwo()",
    include_attributes=True,
)
remove_decl(
    "tests/Features/Infrastructure/FileSystem/DirectoryObjectIdentityResolverTests.cs",
    "public async Task UpgradeLegacyAsync_MismatchedNativeIdentity_FailsClosedWithoutMarker()",
    include_attributes=True,
)
# Foreign-syntax test should exercise only final APIs.
file = "tests/Features/Infrastructure/FileSystem/DirectoryObjectIdentityResolverTests.cs"
replace_once(
    file,
    """        var legacy = await resolver.UpgradeLegacyAsync(
            foreignPath,
            legacyVersion: 1,
            legacyValue: "persisted-foreign-native-identity");

        foreach (var candidate in new[] { resolution, existing, legacy })
""",
    """        foreach (var candidate in new[] { resolution, existing })
""",
)
remove_decl(
    "tests/Features/Infrastructure/Persistence/RootFolderObjectIdentityReconcilerTests.cs",
    "public async Task ReconcileAsync_LegacyVersionTwoIdentityWithoutMarker_RemainsAuthorized()",
    include_attributes=True,
)
remove_decl(
    "tests/Features/Infrastructure/Persistence/RootFolderObjectIdentityReconcilerTests.cs",
    "public async Task ReconcileAsync_MatchingLegacyEnrollmentMarker_RetiresMarkerAndKeepsDatabaseIdentity()",
    include_attributes=True,
)
remove_decl(
    "tests/Features/Infrastructure/Library/Moving/AudiobookContentMoveServiceTests.cs",
    "public async Task MoveContents_SourceAtManagedRoot_DoesNotCreateRootEnrollmentMarker()",
    include_attributes=True,
)
replace_once(
    "tests/Features/Infrastructure/Library/Moving/AudiobookContentMoveServiceTests.cs",
    "            || string.Equals(name, ManagedDirectoryEnrollment.FileName, StringComparison.Ordinal)\n",
)

# Keep useful final-behavior tests, but remove assertions that existed only to prove
# the discarded marker was not written.
for path in (
    "tests/Features/Application/Audiobooks/RootFolders/RootFolderServiceTests.cs",
    "tests/Features/Infrastructure/FileSystem/DirectoryObjectIdentityResolverTests.cs",
    "tests/Features/Infrastructure/Library/Moving/RootFolderRelocationServiceTests.cs",
    "tests/Features/Infrastructure/Persistence/RootFolderObjectIdentityReconcilerTests.cs",
):
    remove_false_marker_assertions(path)

# One relocation test used the literal marker only as a negative side-effect assertion.
relocation_tests = "tests/Features/Infrastructure/Library/Moving/RootFolderRelocationServiceTests.cs"
text = read(relocation_tests)
text = text.replace(
    """        var replacementEnrollment = Path.Join(
            target,
            ".listenarr-root-enrollment.json");
        Assert.False(File.Exists(replacementEnrollment));

""",
    "",
    1,
)
text = text.replace("        Assert.False(File.Exists(replacementEnrollment));\n", "", 1)
text = text.replace(
    "FinalizeCompletedRelocation_ReplacedTargetWithoutEnrollment_DoesNotEnrollReplacement",
    "FinalizeCompletedRelocation_ReplacedTargetWithoutAuthorization_DoesNotCommitReplacement",
    1,
)
write(relocation_tests, text)

# Remaining test-only references should be absent; fail early with a useful list.
for path in Path("tests").rglob("*.cs"):
    source = path.read_text()
    if "ManagedDirectoryEnrollment" in source or ".listenarr-root-enrollment.json" in source or "UpgradeLegacyAsync" in source:
        raise SystemExit(f"stale root-enrollment compatibility remains in {path}")
