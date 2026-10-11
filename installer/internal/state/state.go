// Package state records what the updater installed into a game folder, so
// later runs can tell what changed and remove only files they put there. It
// also remembers which game folder was used last.
package state

import (
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
)

// FileName is the state file, relative to the game folder.
const FileName = "BepInEx/LethalAccess.install.json"

type State struct {
	// Version is the installed LethalAccess release. Empty means none.
	Version string `json:"version,omitempty"`
	// Beta is set when this install follows pre-releases too.
	Beta bool `json:"beta,omitempty"`
	// Files maps each installed LethalAccess file (game folder relative,
	// forward slashes) to its SHA-256.
	Files map[string]string `json:"files"`
	// Components maps a component ID to the files the updater installed for it.
	Components map[string][]string `json:"components,omitempty"`
	// Declined lists optional components the user said no to. Quick update
	// checks do not ask for them again; a full update does.
	Declined []string `json:"declined,omitempty"`
	// SetAside lists files (game folder relative) an update renamed out of the
	// way because they were in use. Later runs delete them.
	SetAside []string `json:"set_aside,omitempty"`
	// Source is "local" for an install made with --source (a test install);
	// its files are expected to differ from GitHub's.
	Source string `json:"source,omitempty"`
}

func path(gameDir string) string { return filepath.Join(gameDir, filepath.FromSlash(FileName)) }

// Load reads the state, returning an empty state when none exists yet.
func Load(gameDir string) (*State, error) {
	s := &State{}
	data, err := os.ReadFile(path(gameDir))
	if err != nil && !errors.Is(err, fs.ErrNotExist) {
		return nil, err
	}
	if err == nil {
		if err := json.Unmarshal(data, s); err != nil {
			return nil, err
		}
	}
	if s.Files == nil {
		s.Files = map[string]string{}
	}
	if s.Components == nil {
		s.Components = map[string][]string{}
	}
	return s, nil
}

// Save writes the state atomically.
func (s *State) Save(gameDir string) error {
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	p := path(gameDir)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	tmp := p + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, p)
}

func (s *State) IsDeclined(id string) bool {
	for _, d := range s.Declined {
		if d == id {
			return true
		}
	}
	return false
}

func (s *State) SetDeclined(id string, declined bool) {
	kept := s.Declined[:0]
	for _, d := range s.Declined {
		if d != id {
			kept = append(kept, d)
		}
	}
	if declined {
		kept = append(kept, id)
	}
	s.Declined = kept
}

// Settings are the updater's own, kept per Windows user.
type Settings struct {
	// GameDir is the game folder used last.
	GameDir string `json:"game_dir,omitempty"`
}

// SettingsDir is the folder holding the settings and the last run's log.
func SettingsDir() string {
	base, err := os.UserCacheDir() // %LOCALAPPDATA%
	if err != nil {
		base = os.TempDir()
	}
	return filepath.Join(base, "LethalAccess")
}

func settingsPath() string { return filepath.Join(SettingsDir(), "updater.json") }

// LoadSettings returns the saved settings, or empty ones.
func LoadSettings() Settings {
	var s Settings
	if data, err := os.ReadFile(settingsPath()); err == nil {
		json.Unmarshal(data, &s)
	}
	return s
}

// Save writes the settings. Failing to is not worth stopping an update for.
func (s Settings) Save() {
	if err := os.MkdirAll(SettingsDir(), 0o755); err != nil {
		return
	}
	if data, err := json.MarshalIndent(s, "", "  "); err == nil {
		os.WriteFile(settingsPath(), data, 0o644)
	}
}
