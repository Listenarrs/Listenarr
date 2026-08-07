from pathlib import Path
import re

payload = Path('listenarr.infrastructure/Library/Moving/LibraryDirectoryOwnershipMarker.Payload.cs')
text = payload.read_text()
text, count = re.subn(
    r'\n\s*internal static bool MatchesLegacyPayload\(.*?ownership\.GetIdentity\(\)\.Semantics\);\n',
    '\n',
    text,
    count=1,
    flags=re.S,
)
if count != 1:
    raise SystemExit('could not remove expression-bodied legacy marker matcher')
payload.write_text(text)

script = Path('.github/scripts/pr717_cleanup_step5.py')
source = script.read_text()
source, count = re.subn(
    r'\nremove_decl\(\n\s*"listenarr\.infrastructure/Library/Moving/LibraryDirectoryOwnershipMarker\.Payload\.cs",\n\s*"internal static bool MatchesLegacyPayload\(",\n\)\n',
    '\n',
    source,
    count=1,
)
if count != 1:
    raise SystemExit('could not suppress legacy matcher helper call')

namespace = {'__name__': '__main__', '__file__': str(script)}
exec(compile(source, str(script), 'exec'), namespace)

replacement = Path('listenarr.infrastructure/Library/Moving/EfLibraryDirectoryOwnershipStore.MarkerlessReplacement.cs')
text = replacement.read_text()
old = '''        if (!await db.LibraryDirectoryOwnershipRetiredMarkers.AnyAsync(
                marker => marker.OwnershipId == stale.Id,
                cancellationToken))
        {
            if (stale.ManagedRootFolderId.HasValue
                && stale.DirectoryObjectIdentityVersion.HasValue
                && !string.IsNullOrWhiteSpace(stale.DirectoryObjectIdentity))
            {
                db.LibraryDirectoryOwnershipRetiredMarkers.Add(
                    LibraryDirectoryOwnershipRetiredMarkerEvidence.Create(
                        stale,
                        new LibraryDirectoryOwnershipMarker.MarkerPayload(
                            LibraryDirectoryOwnershipMarker.Version,
                            stale.OwnershipToken,
                            stale.CanonicalPath,
                            stale.ManagedRootFolderId,
                            stale.DirectoryObjectIdentityVersion,
                            stale.DirectoryObjectIdentity),
                        now));
            }
            else
            {
                db.LibraryDirectoryOwnershipRetiredMarkers.Add(
                    LibraryDirectoryOwnershipRetiredMarkerEvidence
                        .CreateLegacyPending(stale));
            }
        }
'''
new = '''        if (!stale.ManagedRootFolderId.HasValue
            || stale.DirectoryObjectIdentityVersion != ManagedDirectoryIdentity.CurrentVersion
            || string.IsNullOrWhiteSpace(stale.DirectoryObjectIdentity))
        {
            throw new InvalidOperationException(
                "The stale ownership row does not contain the final durable identity required for retirement.");
        }
        if (!await db.LibraryDirectoryOwnershipRetiredMarkers.AnyAsync(
                marker => marker.OwnershipId == stale.Id,
                cancellationToken))
        {
            db.LibraryDirectoryOwnershipRetiredMarkers.Add(
                LibraryDirectoryOwnershipRetiredMarkerEvidence.Create(
                    stale,
                    new LibraryDirectoryOwnershipMarker.MarkerPayload(
                        LibraryDirectoryOwnershipMarker.Version,
                        stale.OwnershipToken,
                        stale.CanonicalPath,
                        stale.ManagedRootFolderId,
                        stale.DirectoryObjectIdentityVersion,
                        stale.DirectoryObjectIdentity),
                    now));
        }
'''
if old not in text:
    raise SystemExit('missing markerless replacement legacy retired-evidence fallback')
replacement.write_text(text.replace(old, new, 1))
