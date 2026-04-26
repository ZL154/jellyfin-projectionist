# Projectionist

A Jellyfin plugin that plays preroll videos before movies **and** TV episodes. Folder-based source — no Jellyfin library required. Custom-designed admin UI.

> Built because every other Jellyfin preroll plugin either (a) requires you to create a Jellyfin library full of preroll files that then clutters your homepage, or (b) only works for movies.

## Features

- **Just point at a folder.** Drop preroll MP4s anywhere on disk Jellyfin can read; configure the path; done. No library to create, no homepage clutter.
- **Episode support.** First-class support for TV episodes, not just movies.
- **Selection modes.** Random / Sequential round-robin / Weighted (favor older files so newly-added prerolls don't dominate).
- **Session behaviour.** Choose: every playback / first of session (idle gap of 20 min triggers next) / once per day per user. Stops the binge-spam problem.
- **Runtime filters.** Skip prerolls before short clips/extras with a min-feature-runtime filter.
- **Custom admin UI.** Properly designed dark interface with file list, status badges, live preview button. Not the default Jellyfin form.
- **Built on the official `IIntroProvider` API.** No queue manipulation, no session hacks, no ABI surprises.

## Install

### Manual install (current recommendation while iterating)

1. Download or build the DLL: `Jellyfin.Plugin.Projectionist.dll`
2. Copy it into your Jellyfin plugins folder:
   - Docker: `<config-volume>/plugins/Projectionist/`
   - Bare metal: `<jellyfin-data>/plugins/Projectionist/`
3. Restart Jellyfin.
4. Dashboard → Plugins → Projectionist → configure.

### Plugin manifest (later)

Add this URL to **Dashboard → Plugins → Repositories** once published:

```
(not yet published)
```

## Configuration

Open the plugin settings page and:

1. Set the **Preroll folder path** to the folder containing your `.mp4`/`.mkv`/`.mov`/etc. files.
2. Pick which content types should trigger a preroll (Movies, TV episodes, Music videos).
3. Choose the **Selection mode** and **Session behaviour**.
4. Save.

The status pill under the folder field will tell you whether the folder is found and how many prerolls were discovered.

## Building from source

```bash
git clone <this-repo>
cd Jellyfin-Prerolls
dotnet build src/Projectionist/Projectionist.csproj -c Release
# DLL output: src/Projectionist/bin/Release/net8.0/Jellyfin.Plugin.Projectionist.dll
```

Targets Jellyfin 10.10 ABI (`Jellyfin.Controller` 10.10.0).

## How it works

The plugin implements `MediaBrowser.Controller.Library.IIntroProvider`. Jellyfin's media server natively supports queueing intros before any feature item. When playback starts, Jellyfin asks every registered `IIntroProvider` for a list of items to play first.

Projectionist's provider:
1. Checks the requested item's content type against the enabled-content-type config.
2. Checks per-user session state (FirstOfSession / OncePerDay).
3. Scans the configured folder (cached for 30 seconds).
4. Picks N files according to the selection mode.
5. Returns `IntroInfo` entries for each pick.

For each pick we first try `LibraryManager.FindByPath` so that if the file already lives in a Jellyfin library, the player gets a proper `BaseItem.Id` (best client compatibility). If the file is outside any library, we fall back to a path-only `IntroInfo` with a deterministic Guid derived from the file path.

## Why "Projectionist"?

Because that's the role this plugin plays — the projectionist is the person who threads the reels and decides what plays before the feature.

## License

MIT
