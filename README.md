# LethalAccess: Lethal Company accessibility for blind and visually impaired players

LethalAccess is a BepInEx mod that makes Lethal Company playable with a screen reader.

## What it does

In the menus, LethalAccess:

- shows the game's menus to Windows as real controls, so your screen reader reads them in its own words, with each control's position in its section
- lets you move with the arrow keys, Tab and Ctrl+Tab, and back out of a screen with Escape
- makes the keybinds screen, host settings, the server list, save files and the news pop-up usable
- adds a menu for inviting Steam friends, since the Steam overlay isn't accessible
- skips the boot-up screen and the cold open scenes, so the game goes straight to the main menu

In a match, LethalAccess:

- reads the terminal, the clipboard, control tips, what you're looking at and what you can interact with
- announces what you pick up and its value, items you sell, the ship landing and leaving, entering and leaving the facility, the moon you route to, and the end-of-round stats
- speaks your health, the time and the profit quota on request
- gives you a list of nearby objects in categories (items, the factory, the elevator, the Company building) and walks you to the one you pick, with sounds that tell you when you're on target. Press P again and it walks you there itself, jumping gaps, climbing ladders, crouching under low beams, crawling under server racks, opening doors and riding the Mineshaft elevator. In guided mode it calls out "Jump", "Crouch" and "Ledge" and helps you time gap jumps
- has quick keys that walk you to the main entrance, its exit, the ship or the nearest item
- marks facility rooms as you explore them, so you can find rooms you haven't been to or retrace your steps
- plays an optional sound from the north and speaks the direction you face
- turns with the arrow keys, holds mouse clicks on the Alt keys, plays a tone while you hold an interaction, and lets you look further down
- makes the belt bag and ship build mode usable from the keyboard
- when you host, lands and launches the ship faster (other players see the normal speed)

Speech outside the menus goes through Lethal Speech Output, which uses Tolk to send it to your screen reader.

## Installing

1. Download `LethalAccessUpdater.exe` from the latest `updater-v` release on the [releases page](https://github.com/GreenBeanGravy/LethalAccess-Dev/releases).
2. Run it. It finds Lethal Company through Steam (or asks you for the folder), installs BepInEx 5.4.23.5 if it's missing, asks if you want LethalCompany InputUtils 0.7.10, then installs LethalAccess.
3. Start Lethal Company as usual.

InputUtils is optional. With it, the LethalAccess keys show up on the game's own keybinds screen.

Run the updater again any time to update. It checks for a new version of itself first, then for a new release of the mod, and it repairs the install if any of the mod's files are missing or changed. If Lethal Company is running, it waits for you to close it. It keeps its settings and a log in `%LOCALAPPDATA%\LethalAccess`.

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

Install BepInEx 5 into the Lethal Company folder, then unzip `LethalAccess.zip` from the latest release into the same folder. The zip's `BepInEx` folder merges with the one already there, and `accesskit.dll` goes next to `Lethal Company.exe`.

## Default keybinds

### Menus

| Key | Action |
| --- | --- |
| Tab | Next control (Ctrl+Tab moves by section) |
| Escape | Back out of a menu screen |
| F10 | LethalAccess settings menu |

### Navigation

| Key | Action |
| --- | --- |
| Right bracket / Left bracket | Next / previous object |
| Equals / Minus | Next / previous category |
| Semicolon | Refresh category |
| P | Walk to the selected object. Press again to switch between following sounds yourself and walking there automatically |
| O | Stop looking and pathfinding |
| 5, 6, 7, 8 | Walk to the main entrance, its exit, the ship, the nearest item |
| 9, 0 | Quick navigation 5 and 6 (no destination until you set one in the config file) |
| M | Cycle room markers: Explore, Retrace, Off |
| U | Announce nearby room markers |
| R | Announce current room |
| Backspace | Toggle visual markers |

Explore marks rooms you haven't visited yet, and Retrace marks the ones you have.

### Information

| Key | Action |
| --- | --- |
| L | Speak direction |
| T | Speak time |
| H | Speak health |
| K | Speak profit quota |
| Quote | Speak control tips |
| N | Toggle north sound |

### Movement and mouse

| Key | Action |
| --- | --- |
| Left arrow / Right arrow | Turn left / right |
| Left Alt | Hold left click |
| Right Alt | Hold right click |

### Changing keys

With InputUtils installed, open the game's keybinds screen. The LethalAccess keys are in their own LethalAccess section, and you rebind, reset and save them there like any other key. The screen reads each row's name and current key, and tells you when a new key conflicts with another binding.

Without InputUtils, change keys in the F10 menu or in `BepInEx/config/Green.LethalAccess.cfg`.

## Troubleshooting

- The game closes on its own soon after it starts: run the updater again. It sets `HideManagerGameObject = true` in `BepInEx/config/BepInEx.cfg`; with it false, Lethal Company unloads every mod at the launch screen. You can also change that line by hand.
- Nothing is spoken at all: run the updater again. It repairs missing or changed files, and installs BepInEx if it's gone.
- Menus speak, but your screen reader's own reading of them is missing: check that `accesskit.dll` is next to `Lethal Company.exe`. Without it the menus fall back to LethalAccess's own speech. The updater puts it back.
- The mod loads twice or keys fire twice: another copy of LethalAccess is in `BepInEx/plugins`. The updater disables stray copies by renaming them to `.disabled`.
- Something else: the BepInEx log is `BepInEx/LogOutput.log` in the game folder. Attach it to an issue.

## Building from source

You need Visual Studio 2022 (or its Build Tools) with .NET Framework 4.x targeting, the .NET SDK, and Go 1.26 or newer for the updater.

UnityA11y lives in its own repository, [GreenBeanGravy/UnityA11y](https://github.com/GreenBeanGravy/UnityA11y), and is included here as a git submodule. Clone with `git clone --recurse-submodules`, or run `git submodule update --init` in a clone you already have.

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
By opening a pull request you agree to the contribution terms in section 4 of [LICENSE](LICENSE).

## License

LethalAccess and UnityA11y are source-available under the [LethalAccess Source License](LICENSE). In short:

- You can use the mod, read the code, change it, and share your own versions or reuse its code in other projects.
- Anything you share has to stay open source under the same license, credit LethalAccess by GreenBeanGravy with a link to this repository, and say what you changed.
- No commercial use: you can't sell it, charge for it or make money from it in any other way. Playing, streaming or recording games with it is fine.
- No patents, and no claiming the original work as your own.

The license text is what counts; this summary isn't a substitute for it.

AccessKit, Tolk, the NVDA controller client, BepInEx, HarmonyX, InputUtils and the Go libraries in the updater keep their own licenses. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the [licenses](licenses) folder.

LethalAccess is a fan-made mod and is not affiliated with or endorsed by Zeekerss, the developer of Lethal Company.
