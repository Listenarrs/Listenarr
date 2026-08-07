from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text()


def write(path: str, text: str) -> None:
    Path(path).write_text(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    if old not in text:
        raise SystemExit(f"missing expected block in {path}: {old[:100]!r}")
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
    line_start = text.rfind("\n", 0, position) + 1
    start = line_start
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


def remove_test_call(path: str, title: str) -> None:
    text = read(path)
    token = f"  it('{title}'"
    position = text.find(token)
    if position < 0:
        raise SystemExit(f"missing vitest {title!r}")
    arrow = text.find("=>", position)
    brace = text.find("{", arrow)
    end = find_matching_brace(text, brace)
    while end < len(text) and text[end] in " \t\r\n":
        end += 1
    if text.startswith(")", end):
        end += 1
    if text.startswith(";", end):
        end += 1
    while end < len(text) and text[end] in " \t\r\n":
        end += 1
    write(path, text[:position] + text[end:])


replace_once(
    "listenarr.application/Audiobooks/Contracts/IRootFolderRelocationService.cs",
    """    Task<RootFolderPathChangeResult> ReauthorizeLegacyTargetAsync(
        Guid relocationId,
        string confirmedTargetPath,
        CancellationToken cancellationToken = default);

""",
    "",
)
replace_once(
    "listenarr.application/Audiobooks/Contracts/RootFolderRelocationPublicProjection.cs",
    """            TargetIdentityEnrollmentState.LegacyUnenrolled =>
                "The relocation target must be reauthorized before the relocation can continue.",
""",
    "",
)
replace_once(
    "listenarr.domain/Audiobooks/RootFolderRelocation.cs",
    "    LegacyUnenrolled,\n",
    "",
)
remove_decl(
    "listenarr.domain/Audiobooks/RootFolderRelocation.cs",
    "public static class TargetIdentityEnrollment",
)
replace_once(
    "listenarr.infrastructure/Persistence/RootFolderObjectIdentityReconciler.cs",
    """        var relocations = await db.RootFolderRelocations
            .ToListAsync(cancellationToken);
        foreach (var relocation in relocations)
        {
            relocation.TargetIdentityEnrollmentState =
                TargetIdentityEnrollment.Classify(relocation);
        }

""",
    "",
)
replace_once(
    "listenarr.infrastructure/Library/Moving/RootFolderRelocationService.Retry.cs",
    """        if (relocation.TargetIdentityEnrollmentState
            == TargetIdentityEnrollmentState.LegacyUnenrolled)
        {
            throw new InvalidOperationException(
                "The legacy relocation target must be explicitly reauthorized before retry.");
        }
""",
    "",
)
replace_once(
    "listenarr.api/Features/Library/RootFolderRelocationsController.cs",
    "    public sealed record ReauthorizeLegacyTargetRequest(string ConfirmedTargetPath);\n\n",
    "",
)
remove_decl(
    "listenarr.api/Features/Library/RootFolderRelocationsController.cs",
    "public async Task<IActionResult> ReauthorizeLegacyTarget(",
    include_attributes=True,
)

reauthorization = Path(
    "listenarr.infrastructure/Library/Moving/RootFolderRelocationService.Reauthorization.cs"
)
if not reauthorization.exists():
    raise SystemExit("missing relocation reauthorization implementation")
reauthorization.unlink()

replace_once(
    "fe/src/types/index.ts",
    "  targetIdentityEnrollmentState: 'NotRequired' | 'Authorized' | 'LegacyUnenrolled' | 'Unavailable'\n",
    "  targetIdentityEnrollmentState: 'NotRequired' | 'Authorized' | 'Unavailable'\n",
)
remove_decl(
    "fe/src/services/api.ts",
    "async reauthorizeLegacyRootFolderRelocationTarget(",
)
remove_decl(
    "fe/src/stores/rootFolders.ts",
    "async function reauthorizeLegacyTarget(",
)
replace_once(
    "fe/src/stores/rootFolders.ts",
    "    reauthorizeLegacyTarget,\n",
    "",
)

vue = "fe/src/components/settings/RootFoldersSettings.vue"
replace_once(
    vue,
    """              <button
                v-if="canReauthorizeLegacyTarget(folder)"
                type="button"
                class="btn btn-secondary"
                data-cy="reauthorize-relocation-target"
                @click="confirmLegacyTargetReauthorization(folder)"
              >
                Reauthorize target
              </button>
""",
    "",
)
replace_once(
    vue,
    """    <DeleteConfirmationModal
      :visible="relocationToReauthorize !== null"
      title="Reauthorize relocation target"
      confirm-text="Reauthorize target"
      @close="relocationToReauthorize = null"
      @confirm="executeLegacyTargetReauthorization"
    >
      <template #confirm-icon><PhShieldCheck /></template>
      <template #default>
        <p>
          Confirm that this is the exact target directory you intend to authorize for the pending
          relocation:
        </p>
        <p>
          <code class="reauthorization-target-path" data-testid="reauthorization-target-path">{{
            relocationToReauthorize?.targetPath
          }}</code>
        </p>
        <p>This authorization also retries the pending relocation.</p>
      </template>
    </DeleteConfirmationModal>
""",
    "",
)
replace_once(
    vue,
    "import type { RootFolder, RootFolderPathChangeResult } from '@/types'\n",
    "import type { RootFolder } from '@/types'\n",
)
replace_once(
    vue,
    """const relocationToReauthorize = ref<{
  relocationId: string
  targetPath: string
} | null>(null)
""",
    "",
)
for needle in (
    "function canReauthorizeLegacyTarget(",
    "function confirmLegacyTargetReauthorization(",
    "async function executeLegacyTargetReauthorization(",
):
    remove_decl(vue, needle)

api_test = Path("fe/src/__tests__/api.rootFolderRelocationReauthorization.spec.ts")
if not api_test.exists():
    raise SystemExit("missing dedicated relocation reauth api test")
api_test.unlink()
remove_test_call(
    "fe/src/__tests__/RootFoldersSettings.spec.ts",
    "shows legacy reauthorization separately and confirms the exact target path",
)

for method in (
    "ReauthorizeLegacyTarget_ExistingMoveJob_BindsConfirmedTargetGenerationBeforeRetry",
    "ReauthorizeLegacyTarget_ContradictoryChildAuthorization_RejectsBeforeTargetEnrollment",
    "ReauthorizeLegacyTarget_RequestCancelledAfterAuthorization_CompletesRetry",
):
    remove_decl(
        "tests/Features/Infrastructure/Library/Moving/RootFolderRelocationServiceTests.cs",
        method,
        include_attributes=True,
    )

path = Path("tests/Features/Domain/Audiobooks/RootFolderRelocationStateTests.cs")
text = path.read_text()
start = text.find(
    "    [Theory]\n    [InlineData(\n        RootFolderRelocationStatus.Pending"
)
if start < 0:
    raise SystemExit("missing target enrollment classifier theory")
class_close = text.rfind("}")
if class_close <= start:
    raise SystemExit("invalid classifier test class structure")
path.write_text(text[:start] + text[class_close:])
