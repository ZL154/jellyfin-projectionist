# Contributing to Projectionist

Quick notes for filing useful bug reports + PRs.

## Bug reports

Open an issue with:
- Jellyfin version (Dashboard -> About).
- Projectionist version (Dashboard -> Plugins).
- Browser + OS (the preroll hook is web-only - native clients don't trigger the JS hook).
- Steps to reproduce.
- Jellyfin server log excerpt during the failing playback (grep for "[Projectionist]").
- Browser dev console output during the failing playback.

## Feature requests

Open an issue describing the use case. Concrete > abstract.

## Pull requests

1. Fork + branch.
2. Build must succeed with -warnaserror.
3. Tests must pass.
4. New behavior gets a test.
5. Update CHANGELOG.md under [Unreleased].
6. Match the existing code style (.editorconfig).
7. Keep diffs focused.

## Local dev

git clone https://github.com/ZL154/jellyfin-projectionist
cd jellyfin-projectionist
dotnet restore
dotnet build src/Projectionist/Projectionist.csproj -c Release

Drop the DLL into your Jellyfin <config>/plugins/Projectionist_<X.Y.Z>.0/ folder and restart.
