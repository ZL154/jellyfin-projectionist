# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.0] - unreleased

1.2.0 was never published to the catalog, so this is the first release
that ships everything below as well as the 1.2.0 section.

### Added

- **Jellyfin 12 support.** Every release now ships two packages from one
  source tree: `X.Y.Z.0` (.NET 9, Jellyfin 10.11) and `X.Y.Z.1` (.NET 10,
  Jellyfin 12). The catalog installs the right one automatically.
- Episode prerolls on modern Jellyfin Web (10.9+, including 12), where
  `playbackManager` is no longer a global: the hook now finds it through
  the webpack module cache or Jellyfin's event bus.
- Auto-played next episodes get their own preroll, and the queue is
  pre-warmed near the end of an episode so the hand-off doesn't flash the
  home screen.
- Series-level opt-out: one entry covers every season and episode.
- "Browse Library…" picker in the admin page for opt-outs; opt-outs are
  listed by name instead of GUID.

### Fixed

- **Web UI broken on Jellyfin 10.11.11+ and 12 ("failed to decode")**
  ([#7](https://github.com/ZL154/jellyfin-projectionist/issues/7)). Those
  versions compress `index.html`, and the script tag was appended to the
  compressed bytes.
- Skip-rate reports and the admin preview returned 401 on Jellyfin 12,
  which only accepts the `Authorization: MediaBrowser Token=` header.
- Player settings (skip delay, skip on/off, preload mode) were never loaded
  when the page was first opened on the login screen.
- The skip button could reappear over the episode after skipping a
  preroll, where clicking it jumped to the end of the episode.
- Playing an episode from a season list started at the season opener
  instead of the chosen episode (`startIndex` ignored).
- A preroll could get prerolls of its own when queued client-side.
- Native TV/mobile apps flashed a "Playback Error" when given episode
  intros they can't play; episode intros are now only served to
  web-based clients (movies are unchanged).
- Admin page header showed a hard-coded version; it now reads the
  installed one.

### Changed

- Removed the experimental server-side stream-concat / HLS-splice code
  path. It had been disabled since it broke subtitles and seeking.
- CI builds and tests both target frameworks, and treats warnings as
  errors again (it had been failing since June on an obsolete-API warning).
- Dependabot no longer proposes Jellyfin package bumps; those pick the
  ABI and are moved by hand.

## [1.2.0] - never released

### Added

- Genre-aware library rules: match prerolls by feature genre.
- Per-feature opt-out: mark items as "never preroll this".
- Skip-rate analytics: track at what second users skip prerolls.
- Outro / post-roll system: play videos after the feature ends.
- Coming-soon trailer: prepend a trailer for an unwatched movie.
- Audio loudness analysis MVP via ffmpeg volumedetect.
- ~30 unit tests across MaturityRanker, ScheduleRule, CooldownStore, PrerollSelector.
- CI workflow with dotnet build -warnaserror + dotnet test on PRs.
- European maturity ratings: FSK, CSA, ACB, Medierådet, ICAA, Eirin.
- Repo hygiene: SECURITY.md, CONTRIBUTING.md, issue templates, PR template, Dependabot.

### Fixed

- Skip button now appears during preroll playback (race condition resolved).
- PrerollSelector uses Random.Shared (thread-safe) instead of static Random.
- HiddenLibraryManager.FindItem no longer falls back to ambiguous filename match.
- HideFromAllUsersAsync gated by SemaphoreSlim.
- Schedule rules log warnings on invalid MM-DD inputs.
- Discovery skips hidden directories.
- CooldownStore prunes entries older than 30 days.

### Changed

- MaturityRanker lifted from nested class to top-level public class.
- MD5 GUID derivation uses MD5.HashData.
- README: badges + FAQ + Compatibility detail + FileTransformation explainer.
- Release zip bundles LICENSE + CHANGELOG.md.

## [1.1.1] - 2026-05-31

### Fixed

- ABI compatibility shim for Jellyfin 10.11.9+ removal of IUserManager.Users.

## [1.1.0] - 2026-05-15

### Added

- Separate session behaviour for movies vs episodes.
- Feature Preload modes (Off / Warm / Hot).
- Dashboard sidebar entry.

## [1.0.2] - 2026-04-26

### Fixed

- Maturity gate no longer wrongly excludes untagged prerolls.
- Library tile hidden from home screen.

## [1.0.0] - 2026-04-21

Initial stable release.

[Unreleased]: https://github.com/ZL154/jellyfin-projectionist/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/ZL154/jellyfin-projectionist/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/ZL154/jellyfin-projectionist/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/ZL154/jellyfin-projectionist/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/ZL154/jellyfin-projectionist/compare/v1.0.0...v1.0.2
[1.0.0]: https://github.com/ZL154/jellyfin-projectionist/releases/tag/v1.0.0
