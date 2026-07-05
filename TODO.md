# Listenarr Fork — TODO

## Known Bugs

_(none open)_

## Fixed

### Sidebar nav: sub-branch items don't react until the parent top node is hovered
- **Fixed 2026-07-04** (canary `c9245e6b`, upstream PR #734). Two causes in `fe/src/App.vue`:
  `.nav-subitem` had no `:hover` / `:focus-visible` style at all (only default + `.active`), and
  `.nav-sub` was collapsed with `pointer-events: none` until opened by a parent hover. Added the hover
  highlight and made the sub-navs stay expanded so children are always interactive.

## Enhancements
- **Semi-static cover/author image cache** (Part 4 of `.claude/plans/books-page-freeze-fix.md`):
  pre-warm all grid thumbnails, durable "no-cover" marker, serve thumbs via static-file middleware.
  Makes cover browsing fast on first paint. Builds on the shipped CoverThumbnailService.
- **Oversized-thumbnail byte guard** (nice-to-have, 2026-07-05). `CoverThumbnailService` resizes to
  400px and encodes at `JpegQuality = 80`, which is fine for 99.8% of covers (avg 34KB). But 2 of
  1076 thumbs are pathological (B001JP7WJC = 731KB at 300x400, B002M4H17M = 617KB at 267x400): the
  source art won't compress at Q80. Add a post-encode guard: if the output exceeds a cap (say 150KB),
  re-encode at a lower quality (or smaller max edge) so no "thumbnail" is near original size. 0.2% of
  the library, purely cosmetic. Verified 2026-07-05 that the grid otherwise serves thumbs correctly
  (e.g. 1570KB original to 65KB via ?size=grid).

## Design (larger, separate from PR #733)
- **Canonical cover identity** (follow-up from PR #733 review, 2026-07-05). The image cache today
  names files by a *sanitized identifier* (`ImageCachePathResolver.SanitizeFileName`), and the
  frontend addresses the same cover under different strings depending on the view: authors by ASIN
  (`/images/{authorAsin}`) vs by name (`ensureAuthorCover`), books by stored `imageUrl` stem vs
  `book.asin` fallback, plus case sensitivity. Result: the same cover is cached under multiple keys.
  ASIN is also not future-proof (authors lack it; non-Audible providers like Phonotheca have none).
  Hashing filenames does NOT fix this on its own (hash of a different input = a different file).
  Real fix = pick ONE canonical key per entity and address every cover by it. Two candidate shapes:
  (A) MediaCover-style internal DB entity id (`images/authors/{authorId}.jpg`), provider-agnostic,
  no hash needed, but does not cover pre-library search-result thumbnails (no entity id yet);
  (B) hash of a canonical provider key (normalized ASIN, else ISBN, else source URL), covers search
  results too. Touches frontend URL builders + backend resolution, so write a plan first. See
  `.claude/reason-notes.md` for the full data-flow trace.
