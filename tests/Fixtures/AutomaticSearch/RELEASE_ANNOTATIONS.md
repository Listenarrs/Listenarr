# Extending release annotations

The production catalog is `listenarr.infrastructure/Search/Matching/release-annotations.json`.
Infrastructure loads the bounded embedded resource once; the application receives
an immutable validated snapshot. Tests load the same resource. There is no remote
catalog refresh, runtime regex configuration, or learning from search results.

Workflow registration keeps `ReleaseAnnotationCatalogProvider` and the automatic
scorer scoped. The provider reads through the application contract
`IReleaseAnnotationCatalogSource`; infrastructure implements that contract and
retains the shared immutable snapshot. The snapshot itself is not a DI service.
Scoring captures one snapshot per assessment, and freshness checks ask the
provider for the current snapshot again. API registration does not call the
embedded-resource loader or contain an infrastructure factory.

Group/source entries have stable IDs, exact names, kinds and permitted placements.
The supported flags are Suffix (existing legacy groups), TechnicalSuffix,
BracketedSuffix, and DelimitedPrefix. Source annotations are bracketed suffixes
only. A new group does not automatically become valid in every layout. The NMR
prefix grammar requires a separate delimited first field and complete independent
book identity; it does not strip the inferred artist.

Example entry, requiring its own reviewed positive and negative fixtures:

```json
{"id":"group.example","name":"ExampleGroup","kind":"Group","placements":"BracketedSuffix"}
```

The loader rejects unknown JSON properties, unknown enum values, duplicate IDs or
names, invalid flags, oversized resources and excessive counts. Invalid catalog
data produces `annotation_catalog_unavailable`; every candidate is still quality
scored and manual search stays available. Startup emits an actionable diagnostic.

The catalog's canonical content hash joins the target fingerprint. Adding an entry
changes that hash automatically. Change `AutomaticSearchAssessment.PolicyVersion`
when changing interpretation semantics, not for each new exact spelling. V6 adds
these rules; its expectations are separate from frozen historical adjudications.

## Series

Exact stored names take precedence. Derived lookup keys support symmetric leading
The, a finite terminal descriptor set, explicit word pairs, and curated aliases.
They never rewrite general book titles or merge stored reference identities.
Multiple distinct references sharing a derived key are ambiguous even if their
positions agree. Explicit numbered claims still have to agree. At most seven
curated aliases belong to one stored name; transformations are nonrecursive.

The catalog retains four original aliases and adds two reference-scoped spellings:
Alex Cross Adventures → Alex Cross, and Women's Murder Club → Womans Murder Club.
These are not general rules deleting Adventures, apostrophes or possessive endings.
The author's catalog independently places Cross Justice in [Alex Cross](https://www.jamespatterson.com/series/alex-cross/)
and The 5th Horseman/The 6th Target in [Women's Murder Club](https://www.jamespatterson.com/series/womens-murder-club/).
The exported reference names and raw historical releases establish the exact
spelling pairs being supported; these pages do not verify downloaded audio.

Unnumbered post-title series fields require a clear bracket/colon slot or the
witnessed `whole known series (complete stored author)` layout. A parenthesized
name in that last layout cannot reuse the stored subtitle as an author credit.
A reference-backed series subtitle may consume one supported descriptor, but not
an ordinal or unrelated text. A bare four-digit field remains numerically ambiguous;
explicit Book/Bk/Vol/Part/# positions remain positions, including four-digit ones.
Literal titles/series with numbers are protected rather than classified as years.

## People and technical annotations

Partial narrator forms come from stored credits. Compound particles and suffixes
are catalog data; hyphenated surnames stay whole. A shortened narrator requires
the existing technical/year/permitted-group scene layout and a unique reference,
with no author collision. Full narrator prefixes require the witnessed comma
layout and a complete stored author and reader; partial prefixes remain deferred.
Collective credits such as Full Cast and Virtual Voice are not surname sources.

Bitrate, size, year and ambiguous legacy bit markers have distinct rule IDs.
`64kb` is not confidently declared a 64 KB file. Recognizing annotations does not
overwrite provider sizes, grant quality points, establish an edition, or remove
language evidence. Audible/retail/GraphicAudio are source annotations; GraphicAudio
alone does not establish a requested Dramatized Adaptation.

## Required evidence

1. Retain raw release text and independently supplied target facts; record provenance.
2. Adjudicate the expected work identity and compatibility separately. Do not use
   imported history or a successful match as ground truth.
3. Add positive, wrong-title/author, role-collision, and ordinal/edition/language
   controls, including raw span offsets. List compound blockers separately.
4. Keep the original historical corpus/facts frozen. Add policy-specific reviewed
   expectations in a separate file, with context and rationale.
5. Replay all exported contexts and held-out Prowlarr pairs. Review every changed
   verdict. Check API/worker handoffs, persisted rows, manual visibility and stale
   reference/catalog fingerprints in fresh containers.
6. Benchmark matcher/context and real scoring separately. Retain input/reference/
   operation limits, cancellation, and fail-closed budget behavior.

Unknown groups, unsupported syntax, missing facts, ambiguous aliases, conflicting
positions/readers and unsupported editions continue to defer. The aim is to
explain justified annotations, not to make every historical release confirm.
