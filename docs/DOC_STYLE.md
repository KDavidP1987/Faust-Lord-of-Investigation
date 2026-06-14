# Documentation style — changelogs, READMEs, Thunderstore page

Faust ships six doc surfaces (see CLAUDE.md → "Release & changelog discipline"). Keep them clean,
scannable, and free of repetition. These rules are authoritative; `tools/preflight.ps1` and the
release hook only remind you of them.

> **Why this exists:** the docs drifted into repetition and over-explaining — the same cross-cutting
> facts restated in every section and a boilerplate footer on every changelog entry. A server admin
> flagged the Thunderstore page as "too complicated / AI slop." This guide is the fix; follow it so it
> doesn't drift back.

## Core principle: say it once

State a cross-cutting fact **one time per document**, never per section or per entry. The repeat
offenders, each of which belongs in exactly one place:

- "Pairs with / built for Raphael"
- "Pre-1.0 / in testing"
- "Admins decide per feature / defaults to admin-only"
- "Feedback at the Shadow Realm Discord"

If you're about to write one of these and it's already in the document, don't.

## Changelogs

- Newest first. One heading per version — `## X.Y.Z (YYYY-MM-DD)` (package) / `## [X.Y.Z] - YYYY-MM-DD`
  (root). Every version gets an entry; no gaps.
- Optional one-line summary, then bullets of user-visible changes. Lead each bullet with a bold 2–5 word
  label (`**Fixed: roaming bosses …**`).
- **No per-entry boilerplate footer** — testing status, "built for Raphael," and the feedback link live
  **once at the top of the file**, not under every version.
- Describe only what changed in **that** version. Don't re-explain a feature shipped earlier ("circling
  back"); link or assume prior context.
- **Package CHANGELOG** (`Faust/Faust/CHANGELOG.md`, Thunderstore) — plain language for players. No
  internal `§` refs, `ApiVersion` numbers, class names, or wire shapes unless a player would care.
  Aim for ≤ ~6 bullets per entry.
- **Root CHANGELOG** (`CHANGELOG.md`, GitHub) — full technical detail is fine and expected (ApiVersion,
  wire shapes, `§` refs, internals).

## READMEs

Fixed section order — don't add overlapping intro/philosophy blocks:

- **Thunderstore** (`Faust/Faust/README.md`): intro → what you can investigate (grouped) → screenshots →
  install → commands → configuration → feedback → acknowledgements/license.
- **GitHub** (`README.md`): intro → status → how it works → feature table → screenshots → architecture →
  layout → building → release discipline → docs → license.

Rules:

- **Intro = one paragraph.** Then a second short paragraph for the Raphael pairing + pre-1.0 note. Stop.
- **Group features** into a few buckets (Castles & plots / Players / Server activity / Bosses & world).
  Don't write one over-explained bullet per feature.
- **Keep the reference tables** (commands, config) — they're not slop. But don't prose-duplicate what a
  table already says.
- **Status sections point to the changelog; they don't recap it.** Never list every past version in a
  paragraph — that's the changelog's job.

## Style

- **Bold for genuine emphasis only**, not decoration. If most of a sentence is bold, none of it lands.
- Active voice, short sentences. Avoid stacking em-dashes (a verbosity / AI-writing tell).
- No marketing repetition. Trust the reader; cut anything that restates a point already made.

## Before a release

1. `pwsh tools/preflight.ps1` — version parity, changelog entries, description length, **plus doc-hygiene
   warnings** (repeated boilerplate, oversized Thunderstore page). Warnings don't block a release, but
   read them.
2. Read the Thunderstore page top-to-bottom once. Does any fact appear more than once? Cut it.
