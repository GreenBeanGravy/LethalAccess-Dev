// LethalAccessUpdater.exe installs and updates LethalAccess in Lethal
// Company: BepInEx, the other mods LethalAccess needs, and LethalAccess
// itself, from the releases on GitHub.
//
// Exit codes: 0 nothing changed, 1 something was updated, 2 error,
// 3 an update is available (--check only).
package main

import (
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"syscall"
	"time"

	"golang.org/x/sys/windows"

	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/console"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/fetch"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/game"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/install"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/manifest"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/selfupdate"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/state"
)

// version is set at build time: -ldflags "-X main.version=1.2.3".
// Development builds ("dev") never replace themselves.
var version = "dev"

const (
	appName      = "LethalAccess Updater"
	restartedEnv = "LETHALACCESS_UPDATER_RESTARTED"
	// Test hooks: a local server standing in for GitHub.
	apiEnv = "LETHALACCESS_UPDATER_API"
	rawEnv = "LETHALACCESS_UPDATER_RAW"

	exitNoUpdate        = 0
	exitUpdated         = 1
	exitError           = 2
	exitUpdateAvailable = 3
)

// rawBase serves files from the repository.
var rawBase = "https://raw.githubusercontent.com"

type options struct {
	quick, check, launch, beta, stable, noComponents bool
	gameDir, source                                  string
}

func main() {
	var opts options
	flag.BoolVar(&opts.quick, "quick", false, "only update when a new LethalAccess release is out or the install needs repair")
	flag.BoolVar(&opts.check, "check", false, "only report whether --quick would update: exit 3 if so, 0 if not")
	flag.BoolVar(&opts.launch, "launch", false, "update quickly, then start Lethal Company")
	flag.BoolVar(&opts.beta, "beta", false, "follow Beta (pre-release) builds from now on")
	flag.BoolVar(&opts.stable, "stable", false, "follow stable releases only from now on (the default)")
	flag.StringVar(&opts.gameDir, "game", "", "the Lethal Company folder (default: found through Steam)")
	flag.StringVar(&opts.source, "source", "", "install this LethalAccess zip instead of the GitHub release, for testing")
	flag.BoolVar(&opts.noComponents, "no-components", false, "skip BepInEx and the other mods, for testing")
	retry := flag.Bool("retry-forever", false, "retry failed downloads until they succeed")
	plain := flag.Bool("console", false, "print progress as text in the console instead of showing a progress window")
	forceWindow := flag.Bool("window", false, "show the progress window even when started from a console or a pipe")
	showVersion := flag.Bool("version", false, "print the updater's version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		return
	}
	if opts.launch {
		opts.quick = true
	}
	if v := os.Getenv(apiEnv); v != "" {
		selfupdate.APIBase = strings.TrimRight(v, "/")
	}
	if v := os.Getenv(rawEnv); v != "" {
		rawBase = strings.TrimRight(v, "/")
	}

	console.SetTitle(appName)
	os.MkdirAll(state.SettingsDir(), 0o755)
	// A restarted updater continues the log of the one that started it.
	console.SetLogFile(filepath.Join(state.SettingsDir(), "updater.log"), os.Getenv(restartedEnv) != "")
	fetch.Persistent = *retry
	install.Progress = func(done, total int) {
		if total > 0 {
			console.Progress(float64(done) * 100 / float64(total))
		}
	}

	// The progress window replaces the console when someone ran the updater
	// (a console made for it). A terminal, a pipe and --console keep the text
	// output, and so does --check, which only reports.
	if !*plain && (*forceWindow || (!opts.check && console.OwnsConsole())) {
		console.UseWindow()
	}
	if opts.check {
		console.Say("Checking for updates...")
	}

	code, err := run(opts)
	if restartedChild {
		os.Exit(code) // the new updater reported everything itself
	}
	if err == nil && code == exitError {
		err = errors.New("the update did not finish")
	}
	if err != nil {
		// --launch still starts the game: a failed update shouldn't stop play.
		if !opts.launch || opts.check {
			console.Fail(exitError, "%v", err)
		}
		console.Say("Error: %v", err)
		code = exitError
	}
	if opts.launch && !opts.check {
		console.Say("Starting Lethal Company.")
		if err := game.Launch(); err != nil {
			console.Say("Could not start Lethal Company: %v", err)
		}
		console.Finish(2 * time.Second)
		os.Exit(code)
	}
	if !opts.check {
		console.Finish(5 * time.Second)
	}
	os.Exit(code)
}

// restartedChild is set when restartSelf ran a newer updater in this one's place.
var restartedChild bool

func run(opts options) (int, error) {
	self, err := os.Executable()
	if err != nil {
		return exitError, err
	}
	selfupdate.CleanupOld(self)

	gameDir, err := findGame(opts.gameDir, filepath.Dir(self))
	if err != nil {
		return exitError, err
	}
	st, err := state.Load(gameDir)
	if err != nil {
		return exitError, fmt.Errorf("could not read the install record: %w", err)
	}
	st.SetAside = install.DeleteSetAside(gameDir, st.SetAside)
	switch {
	case opts.beta:
		st.Beta = true
	case opts.stable:
		st.Beta = false
	}

	m := manifest.BuiltIn()
	if opts.source == "" {
		if os.Getenv(restartedEnv) == "" && !opts.check {
			if restarted, code := updateSelf(self, m, st.Beta); restarted {
				return code, nil
			}
		}
		m = loadManifest(m)
	}

	mod, err := findRelease(m, opts, st)
	if err != nil {
		return exitError, err
	}
	if mod == nil {
		if st.Version != "" && (opts.quick || opts.check) {
			console.Say("No LethalAccess release is on GitHub, so nothing was updated.")
			return exitNoUpdate, nil
		}
		return exitError, fmt.Errorf("no LethalAccess release has been published on GitHub yet, so there is nothing to install")
	}

	healthy := isHealthy(gameDir, m, st, opts.noComponents)
	newer := st.Version == "" || selfupdate.Newer(mod.Version, st.Version) || mod.Version != st.Version && opts.source != ""
	// A test install from --source is replaced by the real release even at
	// the same version.
	switching := st.Version != "" && (mod.Version != st.Version && !newer || st.Source == "local" && opts.source == "")
	if opts.check {
		if newer || switching {
			console.Say("An update is available: LethalAccess %s.", mod.Version)
			return exitUpdateAvailable, nil
		}
		if !healthy {
			console.Say("LethalAccess %s needs repair: files are missing or changed. Run the updater to fix them.", st.Version)
			return exitUpdateAvailable, nil
		}
		console.Say("LethalAccess %s is up to date.", st.Version)
		return exitNoUpdate, nil
	}
	if opts.quick && !newer && !switching && healthy {
		console.Say("LethalAccess %s is up to date.", st.Version)
		return exitNoUpdate, nil
	}

	if !waitForGame(gameDir, opts.quick) {
		return exitNoUpdate, nil
	}

	previous := st.Version
	if previous == "" {
		console.Say("Installing LethalAccess %s into %s", mod.Version, gameDir)
	} else if newer {
		console.Say("Updating LethalAccess from %s to %s.", previous, mod.Version)
	} else if previous != mod.Version {
		// Leaving Beta builds can mean going back a version.
		console.Say("Switching LethalAccess from %s to %s.", previous, mod.Version)
	} else {
		console.Say("Checking LethalAccess %s.", mod.Version)
	}

	changed := false
	if !opts.noComponents {
		c, err := installComponents(gameDir, m, st)
		changed = c
		if err != nil {
			st.Save(gameDir)
			return exitError, err
		}
		c, err = fixBepInExConfig(gameDir)
		if err != nil {
			return exitError, fmt.Errorf("could not fix %s: %w", bepinexConfig, err)
		}
		if c {
			changed = true
			console.Say("Set HideManagerGameObject to true in BepInEx.cfg. Without it Lethal Company unloads every mod at the launch screen.")
		}
	}
	c, err := installMod(gameDir, m, st, mod)
	changed = changed || c
	if saveErr := st.Save(gameDir); err == nil && saveErr != nil {
		err = fmt.Errorf("could not save the install record: %w", saveErr)
	}
	if err != nil {
		return exitError, err
	}
	if !changed {
		console.Say("LethalAccess %s is up to date.", st.Version)
		return exitNoUpdate, nil
	}
	console.Say("LethalAccess %s is installed and up to date.", st.Version)
	if previous != "" && previous != st.Version && mod.Page != "" {
		offerReleaseNotes(mod)
	}
	return exitUpdated, nil
}

// findGame picks the game folder: --game, then the one used last, then a
// search, then the user.
func findGame(flagDir, selfDir string) (string, error) {
	settings := state.LoadSettings()
	remember := func(dir string) string {
		if !strings.EqualFold(settings.GameDir, dir) {
			settings.GameDir = dir
			settings.Save()
		}
		return dir
	}
	if flagDir != "" {
		dir, _ := filepath.Abs(flagDir)
		if !game.IsGameDir(dir) {
			return "", fmt.Errorf("%s is not the Lethal Company folder: it has no %s", dir, game.ExeName)
		}
		return remember(dir), nil
	}
	if settings.GameDir != "" && game.IsGameDir(settings.GameDir) {
		return settings.GameDir, nil
	}
	if dir, err := game.Find(selfDir); err == nil {
		console.Say("Found Lethal Company in %s", dir)
		return remember(dir), nil
	}
	console.Say("Lethal Company wasn't found in your Steam libraries.")
	for {
		dir, ok := console.PickFolder("Choose the Lethal Company folder, the one that holds Lethal Company.exe.")
		if !ok {
			return "", fmt.Errorf("no game folder was chosen. Run the updater again with --game and the Lethal Company folder")
		}
		if game.IsGameDir(dir) {
			return remember(dir), nil
		}
		console.Say("%s has no %s. Choose the folder that does.", dir, game.ExeName)
	}
}

// waitForGame makes sure Lethal Company is closed before files change. A
// quick update gives up instead of waiting.
func waitForGame(gameDir string, quick bool) bool {
	if !game.Running(gameDir) {
		return true
	}
	if quick {
		console.Say("Lethal Company is running, so LethalAccess was not updated. Close the game and run the updater again.")
		return false
	}
	for game.Running(gameDir) {
		if console.InWindow() {
			if !console.Ask("Lethal Company is running. Close the game, then choose Yes to continue the update. Choose No to stop.") {
				console.Say("Stopped: Lethal Company is still running.")
				return false
			}
			continue
		}
		console.Say("Lethal Company is running. The update continues once the game is closed.")
		for game.Running(gameDir) {
			time.Sleep(2 * time.Second)
		}
	}
	return true
}

// loadManifest reads the newest manifest from GitHub, falling back to the
// built-in copy.
func loadManifest(builtIn *manifest.Manifest) *manifest.Manifest {
	data, err := fetch.Bytes(fmt.Sprintf("%s/%s/HEAD/%s", rawBase, builtIn.Repo, manifest.Path))
	if err != nil {
		return builtIn
	}
	m, err := manifest.Parse(data)
	if err != nil {
		console.Say("The install list on GitHub could not be read (%v). Using the built-in one.", err)
		return builtIn
	}
	return m
}

// updateSelf replaces the updater with the newest updater release. When it
// did, it starts the new copy and returns its exit code.
func updateSelf(self string, m *manifest.Manifest, beta bool) (restarted bool, code int) {
	if version == "dev" {
		return false, 0
	}
	rel, err := selfupdate.Latest(m.Repo, selfupdate.UpdaterTagPrefix, beta)
	if err != nil || rel == nil || !selfupdate.Newer(rel.Version, version) {
		return false, 0
	}
	a, ok := rel.Assets[m.UpdaterAsset]
	if !ok {
		return false, 0
	}
	console.Say("Updating the updater to version %s...", rel.Version)
	if err := selfupdate.Replace(self, a); err != nil {
		console.Say("Could not update the updater: %v", err)
		return false, 0
	}
	return true, restartSelf(self)
}

func restartSelf(self string) int {
	os.Setenv(restartedEnv, "1")
	attr := &os.ProcAttr{Env: os.Environ(), Files: []*os.File{os.Stdin, os.Stdout, os.Stderr}}
	args := os.Args
	if console.InWindow() {
		// The new updater shows its own window.
		console.Hide()
		attr.Files = nil
		attr.Sys = &syscall.SysProcAttr{HideWindow: true, CreationFlags: windows.CREATE_NO_WINDOW}
		args = append(slices.Clone(args), "--window")
	}
	restartedChild = true
	p, err := os.StartProcess(self, args, attr)
	if err != nil {
		console.Say("Could not start the new updater: %v", err)
		return exitError
	}
	s, err := p.Wait()
	if err != nil {
		return exitError
	}
	return s.ExitCode()
}

func offerReleaseNotes(mod *modRelease) {
	open := false
	if console.InWindow() {
		open = console.Ask("LethalAccess was updated to " + mod.Version + ". Open the release notes?")
	} else if console.OwnsConsole() {
		console.Say("LethalAccess was updated to %s. Type Y and press Enter to open the release notes, or press Enter to continue.", mod.Version)
		open = strings.EqualFold(console.ReadLine(), "y")
	}
	if open {
		verb, _ := windows.UTF16PtrFromString("open")
		page, _ := windows.UTF16PtrFromString(mod.Page)
		windows.ShellExecute(0, verb, page, nil, nil, windows.SW_SHOWNORMAL)
	}
}
