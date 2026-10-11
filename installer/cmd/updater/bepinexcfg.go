package main

import (
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
)

// bepinexConfig is BepInEx's own settings file.
const bepinexConfig = "BepInEx/config/BepInEx.cfg"

// BepInEx keeps every plugin on one manager object. Lethal Company destroys
// that object on its first scene load unless HideManagerGameObject is true,
// which unloads the mods at the launch screen. BepInEx ships with it false.
const (
	chainloaderSection = "[Chainloader]"
	hideManagerKey     = "HideManagerGameObject"
)

// bepinexConfigOK reports whether BepInEx.cfg already keeps the mods loaded.
func bepinexConfigOK(gameDir string) bool {
	b, err := os.ReadFile(filepath.Join(gameDir, filepath.FromSlash(bepinexConfig)))
	if err != nil {
		return false
	}
	_, changed := fixHideManager(string(b))
	return !changed
}

// fixBepInExConfig sets HideManagerGameObject = true in BepInEx.cfg and leaves
// every other line as it was. With no config yet it writes just that setting;
// BepInEx adds the rest on the next launch and keeps the value.
func fixBepInExConfig(gameDir string) (changed bool, err error) {
	p := filepath.Join(gameDir, filepath.FromSlash(bepinexConfig))
	b, err := os.ReadFile(p)
	if err != nil && !errors.Is(err, fs.ErrNotExist) {
		return false, err
	}
	out, changed := fixHideManager(string(b))
	if !changed {
		return false, nil
	}
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return false, err
	}
	return true, os.WriteFile(p, []byte(out), 0o644)
}

// fixHideManager returns the config text with HideManagerGameObject = true
// under [Chainloader], and whether that changed anything.
func fixHideManager(text string) (string, bool) {
	nl := "\n"
	if strings.Contains(text, "\r\n") || text == "" {
		nl = "\r\n"
	}
	lines := strings.Split(strings.ReplaceAll(text, "\r\n", "\n"), "\n")
	want := hideManagerKey + " = true"

	inSection := false
	header := -1
	for i, line := range lines {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "[") {
			inSection = strings.EqualFold(trimmed, chainloaderSection)
			if inSection {
				header = i
			}
			continue
		}
		if !inSection {
			continue
		}
		key, value, ok := strings.Cut(trimmed, "=")
		if !ok || !strings.EqualFold(strings.TrimSpace(key), hideManagerKey) {
			continue
		}
		if strings.EqualFold(strings.TrimSpace(value), "true") {
			return text, false
		}
		lines[i] = want
		return strings.Join(lines, nl), true
	}

	if header >= 0 {
		lines = append(lines[:header+1], append([]string{want}, lines[header+1:]...)...)
		return strings.Join(lines, nl), true
	}
	if text == "" {
		return chainloaderSection + nl + want + nl, true
	}
	if !strings.HasSuffix(text, "\n") {
		text += nl
	}
	return text + nl + chainloaderSection + nl + want + nl, true
}
