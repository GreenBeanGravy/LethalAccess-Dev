package main

import (
	"archive/zip"
	"errors"
	"fmt"
	"io"
	"maps"
	"os"
	"path"
	"path/filepath"
	"slices"
	"strings"

	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/console"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/fetch"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/install"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/manifest"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/selfupdate"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/state"
)

// versionFile sits at the root of the mod zip and names the release. It is
// read, never installed.
const versionFile = "VERSION"

// pluginsDir is where BepInEx loads mods from.
const pluginsDir = "BepInEx/plugins"

// modRelease is the LethalAccess build to install: a GitHub release, or a
// local zip given with --source.
type modRelease struct {
	Version string
	Page    string
	asset   selfupdate.Asset
	local   string
}

func findRelease(m *manifest.Manifest, opts options, st *state.State) (*modRelease, error) {
	if opts.source != "" {
		v, err := zipVersion(opts.source)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", opts.source, err)
		}
		return &modRelease{Version: v, local: opts.source}, nil
	}
	rel, err := selfupdate.Latest(m.Repo, selfupdate.ModTagPrefix, st.Beta)
	if err != nil {
		return nil, fmt.Errorf("could not check GitHub for LethalAccess releases: %w", err)
	}
	if rel == nil {
		return nil, nil
	}
	a, ok := rel.Assets[m.ModAsset]
	if !ok {
		return nil, fmt.Errorf("the LethalAccess %s release has no %s attached", rel.Version, m.ModAsset)
	}
	if a.SHA256 == "" {
		return nil, fmt.Errorf("the LethalAccess %s release lists no SHA-256 for %s, so the download can't be checked", rel.Version, m.ModAsset)
	}
	return &modRelease{Version: rel.Version, Page: rel.Page, asset: a}, nil
}

// zipVersion reads the VERSION file of a mod zip.
func zipVersion(p string) (string, error) {
	r, err := zip.OpenReader(p)
	if err != nil {
		return "", err
	}
	defer r.Close()
	for _, f := range r.File {
		if f.Name != versionFile {
			continue
		}
		rc, err := f.Open()
		if err != nil {
			return "", err
		}
		defer rc.Close()
		data, err := io.ReadAll(io.LimitReader(rc, 64))
		if err != nil {
			return "", err
		}
		return strings.TrimSpace(string(data)), nil
	}
	return "", errors.New("the zip has no VERSION file")
}

// detected reports whether a component is already in the game folder,
// whoever installed it.
func detected(gameDir string, c manifest.Component) bool {
	if c.DetectName != "" && len(install.FindByName(gameDir, pluginsDir, c.DetectName)) > 0 {
		return true
	}
	if len(c.Detect) == 0 {
		return false
	}
	for _, rel := range c.Detect {
		if _, err := os.Stat(filepath.Join(gameDir, filepath.FromSlash(rel))); err != nil {
			return false
		}
	}
	return true
}

// isHealthy reports whether the install matches its record: every
// LethalAccess file has the contents it was installed with, and every
// component is present or was turned down.
func isHealthy(gameDir string, m *manifest.Manifest, st *state.State, noComponents bool) bool {
	if len(st.Files) == 0 {
		return false
	}
	for rel, want := range st.Files {
		if have, err := install.HashFile(filepath.Join(gameDir, filepath.FromSlash(rel))); err != nil || have != want {
			return false
		}
	}
	if noComponents {
		return true
	}
	if !bepinexConfigOK(gameDir) {
		return false
	}
	for _, c := range m.Components {
		if !detected(gameDir, c) && (c.Required || !st.IsDeclined(c.ID)) {
			return false
		}
	}
	return true
}

// askYesNo asks in the window or a console made for the updater. With no one
// to ask (a pipe, a terminal script), the answer is yes.
func askYesNo(question string) bool {
	if console.InWindow() {
		return console.Ask(question)
	}
	if !console.OwnsConsole() {
		return true
	}
	console.Say("%s Type Y and press Enter for yes, or N for no.", question)
	return !strings.EqualFold(console.ReadLine(), "n")
}

// download fetches url into a new temporary folder and opens it as a zip.
// The returned function closes the zip and deletes the folder.
func download(url, sha, name string) (*zip.ReadCloser, func(), error) {
	dir, err := os.MkdirTemp("", "lethalaccess-")
	if err != nil {
		return nil, nil, err
	}
	p := filepath.Join(dir, "download.zip")
	console.Progress(0)
	if err := fetch.FileProgress(url, p, fetch.Expect{SHA256: sha}, console.BytesProgress()); err != nil {
		os.RemoveAll(dir)
		return nil, nil, fmt.Errorf("downloading %s: %w", name, err)
	}
	r, err := zip.OpenReader(p)
	if err != nil {
		os.RemoveAll(dir)
		return nil, nil, fmt.Errorf("opening %s: %w", name, err)
	}
	return r, func() { r.Close(); os.RemoveAll(dir) }, nil
}

// installComponents installs the components that are missing. Optional ones
// are offered first; a no is remembered for quick updates.
func installComponents(gameDir string, m *manifest.Manifest, st *state.State) (changed bool, err error) {
	for _, c := range m.Components {
		if detected(gameDir, c) {
			continue
		}
		if !c.Required {
			if !askYesNo(fmt.Sprintf("Install %s? %s", c.Name, c.Reason)) {
				st.SetDeclined(c.ID, true)
				console.Say("Skipped %s.", c.Name)
				continue
			}
			st.SetDeclined(c.ID, false)
		}
		console.Say("Downloading %s...", c.Name)
		r, done, err := download(c.URL, c.SHA256, c.Name)
		if err != nil {
			return changed, err
		}
		entries, err := install.Plan(&r.Reader, func(name string) (string, bool) {
			if m.Skipped(name) {
				return "", false
			}
			return c.Target(name)
		})
		if err != nil {
			done()
			return changed, fmt.Errorf("%s: %w", c.Name, err)
		}
		console.Say("Installing %s...", c.Name)
		res, err := install.Apply(gameDir, entries)
		done()
		st.SetAside = append(st.SetAside, res.SetAside...)
		if err != nil {
			return changed, fmt.Errorf("installing %s: %w", c.Name, err)
		}
		st.Components[c.ID] = slices.Sorted(maps.Keys(res.Files))
		changed = true
		console.Say("Installed %s.", c.Name)
	}
	return changed, nil
}

// modTarget says where a mod zip entry goes. Only files under BepInEx are
// installed; VERSION and documentation are not.
// rootFiles are the only files outside BepInEx/ the mod zip may write. Each
// is named here so a release can never overwrite the game's own files.
var rootFiles = map[string]bool{
	"accesskit.dll": true, // native AccessKit, loaded by UnityA11y from the game folder
}

func modTarget(m *manifest.Manifest) func(string) (string, bool) {
	return func(name string) (string, bool) {
		if name == versionFile || m.Skipped(name) {
			return "", false
		}
		if rootFiles[strings.ToLower(name)] {
			return name, true
		}
		if !strings.HasPrefix(strings.ToLower(name), "bepinex/") {
			return "", false
		}
		return name, true
	}
}

// installMod brings the LethalAccess files in line with the release: new and
// changed files are written, files the release dropped are removed, and
// copies of its DLLs elsewhere under BepInEx/plugins are disabled so the game
// doesn't load LethalAccess twice.
func installMod(gameDir string, m *manifest.Manifest, st *state.State, mod *modRelease) (changed bool, err error) {
	var r *zip.Reader
	if mod.local != "" {
		rc, err := zip.OpenReader(mod.local)
		if err != nil {
			return false, err
		}
		defer rc.Close()
		r = &rc.Reader
	} else {
		console.Say("Downloading LethalAccess %s...", mod.Version)
		rc, done, err := download(mod.asset.URL, mod.asset.SHA256, m.ModAsset)
		if err != nil {
			return false, err
		}
		defer done()
		r = &rc.Reader
	}
	entries, err := install.Plan(r, modTarget(m))
	if err != nil {
		return false, fmt.Errorf("%s: %w", m.ModAsset, err)
	}
	if len(entries) == 0 {
		return false, fmt.Errorf("%s holds no files to install", m.ModAsset)
	}

	console.Say("Installing LethalAccess %s...", mod.Version)
	res, err := install.Apply(gameDir, entries)
	st.SetAside = append(st.SetAside, res.SetAside...)
	if err != nil {
		return len(res.Updated) > 0, err
	}
	changed = len(res.Updated) > 0

	var old []string
	for rel := range st.Files {
		if _, ok := res.Files[rel]; !ok {
			old = append(old, rel)
		}
	}
	slices.Sort(old)
	removed, aside := install.Remove(gameDir, old)
	st.SetAside = append(st.SetAside, aside...)
	if len(removed) > 0 {
		changed = true
		console.Say("Removed %d file(s) this version no longer uses.", len(removed))
	}

	if disabled := disableStrays(gameDir, res.Files); len(disabled) > 0 {
		changed = true
		console.Say("Another copy of LethalAccess was found and turned off, so the game loads it only once. Renamed to .disabled: %s", strings.Join(disabled, ", "))
	}

	if st.Version != mod.Version {
		changed = true
	}
	st.Version = mod.Version
	st.Files = res.Files
	st.Source = ""
	if mod.local != "" {
		st.Source = "local"
	}
	return changed, nil
}

// disableStrays renames other copies of the installed DLLs under
// BepInEx/plugins to "<name>.disabled". These come from installing by hand
// into a different folder.
func disableStrays(gameDir string, files map[string]string) []string {
	var out []string
	for rel := range files {
		if !strings.EqualFold(path.Ext(rel), ".dll") {
			continue
		}
		for _, other := range install.FindByName(gameDir, pluginsDir, path.Base(rel)) {
			if strings.EqualFold(other, rel) {
				continue
			}
			p := filepath.Join(gameDir, filepath.FromSlash(other))
			os.Remove(p + ".disabled")
			if os.Rename(p, p+".disabled") == nil {
				out = append(out, other)
			}
		}
	}
	slices.Sort(out)
	return out
}
