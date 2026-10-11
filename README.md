# LethalAccess

LethalAccess is a BepInEx mod that makes Lethal Company playable with a screen reader. It reads menus, the terminal and game events aloud, gives the game's menus keyboard focus a screen reader can follow, and adds audio navigation for finding your way around the ship, the moons and the facility.

Speech goes through Lethal Speech Output, which uses Tolk to send it to your screen reader.

## Installing

Download `LethalAccessUpdater.exe` from the [releases page](https://github.com/GreenBeanGravy/LethalAccess-Dev/releases) and run it. It finds Lethal Company through Steam (or asks you for the folder), then installs:

- BepInEx 5.4.23.5, which the mod needs to load.
- LethalCompany InputUtils 0.7.10, if you say yes. It is optional; with it, the LethalAccess keys show up on the game's own keybinds screen.
- LethalAccess itself.

Run the updater again any time to update. It checks for a new version of itself first, then for a new release of the mod, and it repairs the install if any of the mod's files are missing or changed. If Lethal Company is running, it waits for you to close it.

The updater keeps its settings and a log in `%LOCALAPPDATA%\LethalAccess`.

### Updater options

| Option | What it does |
| --- | --- |
| `--quick` | Only update when a new release is out or the install needs repair. |
| `--check` | Report whether `--quick` would update. Exit code 3 means yes, 0 means no. |
| `--launch` | Update quickly, then start Lethal Company. Good for a desktop shortcut. |
| `--beta` | Follow pre-release builds from now on. |
| `--stable` | Go back to stable releases (the default). |
| `--game <folder>` | Use this Lethal Company folder instead of finding it through Steam. |
| `--console` | Print progress in the console instead of showing a window. |
| `--window` | Show the progress window even when run from a console. |
| `--retry-forever` | Keep retrying failed downloads. |
| `--source <zip>` | Install a local LethalAccess zip instead of the release, for testing. |
| `--no-components` | Skip BepInEx and InputUtils, for testing. |
| `--version` | Print the updater's version. |

Exit codes: 0 means nothing changed, 1 means something was installed or updated, 2 means an error, and 3 means an update is available (with `--check`).

### Installing by hand

Install BepInEx 5 into the Lethal Company folder, then unzip `LethalAccess.zip` from the latest release into the same folder. The zip's `BepInEx` folder merges with the one already there.

## Default keys

| Key | Action |
| --- | --- |
| Tab | Menu navigation |
| Right bracket | Next item |
| Left bracket | Previous item |
| Equals | Next category |
| Minus | Previous category |
| Semicolon | Refresh category |
| P | Go to selected item |
| O | Stop looking and pathfinding |
| 5 to 0 | Quick navigation 1 to 6 |
| Left arrow | Turn left |
| Right arrow | Turn right |
| L | Speak direction |
| R | Announce current room |
| U | Announce nearby navigation points |
| T | Speak time |
| H | Speak health |
| K | Speak profit quota |
| Quote | Speak control tips |
| N | Toggle north sound |
| M | Cycle navigation mode |
| Backspace | Toggle visual markers |
| Left Alt | Left click, hold |
| Right Alt | Right click, hold |
| F10 | LethalAccess settings menu |

The quick navigation keys go to the main entrance (5), the main entrance exit (6), the ship (7) and the nearest item (8). Keys 9 and 0 have no destination until you set one in the config file.

The navigation mode key cycles the facility tile markers between Explore (rooms you have not visited), Retrace (rooms you have) and Off.

### Changing keys

With InputUtils installed, open the game's keybinds screen. The LethalAccess keys are in their own LethalAccess section, and you rebind, reset and save them there like any other key. The screen reads each row's name and current key, and tells you when a new key conflicts with another binding.

Without InputUtils, change keys in the F10 menu or in `BepInEx/config/Green.LethalAccess.cfg`.

## Building from source

You need Visual Studio 2022 (or its Build Tools) with .NET Framework 4.x targeting, the .NET SDK, and Go 1.26 or newer for the updater.

The projects reference the game's DLLs from `_work/gameref`, a folder next to this repository. Copy into it:

- `Lethal Company_Data/Managed` from the game, as `_work/gameref/Managed`.
- `BepInEx/core` from a BepInEx install, as `_work/gameref/BepInEx/core`.
- `LethalCompanyInputUtils.dll` as `_work/gameref/plugins/LethalCompanyInputUtils.dll`.

Then build UnityA11y first and the solution second:

```powershell
dotnet build UnityA11y\UnityA11y.csproj -c Release "-p:ManagedDir=..\_work\gameref\Managed" "-p:BepInExDir=..\_work\gameref\BepInEx\core"
msbuild "LethalAccess Remake.sln" /t:Build /p:Configuration=Release
```

Run MSBuild from PowerShell or a Developer Command Prompt. Git Bash rewrites `/t:Build` into a file path.

### Packaging a release

`tools\package.ps1` builds everything and writes `dist\LethalAccess.zip`. Pass `-GameRef <folder>` if your reference folder is somewhere other than `_work/gameref`. It refuses to run if `VERSION` and `modVersion` in `Plugin.cs` disagree, so bump both together.

```powershell
tools\package.ps1
```

Add `-Publish` to create the GitHub release `v<VERSION>` with the zip attached (needs the `gh` CLI, signed in). Add `-Beta` to mark it as a pre-release; only updaters set to `--beta` will install it. The release notes are the top entry of `CHANGELOG.txt`, which has to be for the version you are releasing, so add the entry before publishing.

### Releasing the updater

The updater is built by GitHub Actions. Push a tag named `updater-vX.Y.Z` and the workflow runs the tests, builds `LethalAccessUpdater.exe` with that version, and attaches it to a new release. Updaters already installed pick it up the next time they run.

To build it locally:

```powershell
cd installer
go build -trimpath -ldflags "-s -w -X main.version=1.0.0" -o LethalAccessUpdater.exe ./cmd/updater
```

## Contributing

Fork the repository, make your changes on a branch, and open a pull request. Test changes in the game with a screen reader before sending them. If you set up the build and something here is wrong or missing, open an issue.
