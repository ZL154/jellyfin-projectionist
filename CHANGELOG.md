# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.0] - 2026-05-31

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
