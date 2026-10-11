// Package manifest describes what the updater installs: the LethalAccess
// release asset and the other mods it needs. The copy built into the updater
// is used when the one on GitHub can't be read, so an old updater keeps
// working while the manifest changes.
package manifest

import (
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"path"
	"strings"
)

// Path is where the manifest lives in the repository.
const Path = "installer/internal/manifest/default.json"

//go:embed default.json
var builtIn []byte

type Manifest struct {
	Schema int    `json:"schema"`
	Repo   string `json:"repo"`
	// ModAsset is the zip attached to each LethalAccess release. Its paths
	// are relative to the game folder.
	ModAsset string `json:"mod_asset"`
	// UpdaterAsset is the updater executable attached to updater releases.
	UpdaterAsset string `json:"updater_asset"`
	// SkipExtensions are never installed from any archive (documentation).
	SkipExtensions []string    `json:"skip_extensions"`
	Components     []Component `json:"components"`
}

// Component is another mod that LethalAccess needs or works better with.
type Component struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Reason string `json:"reason"`
	// Required components are installed even after the user declined them.
	Required bool `json:"required"`
	// Detect lists files, relative to the game folder, that all exist once
	// the component is installed.
	Detect []string `json:"detect,omitempty"`
	// DetectName is a file name that marks the component when found
	// anywhere under BepInEx/plugins, wherever the user put it.
	DetectName string `json:"detect_name,omitempty"`
	URL        string `json:"url"`
	SHA256     string `json:"sha256"`
	// Map turns archive paths into game folder paths: an archive entry
	// starting with a key is installed at the value plus the rest of its
	// path. Entries matching no key are skipped. "" maps everything.
	Map map[string]string `json:"map"`
}

// BuiltIn returns the manifest compiled into the updater.
func BuiltIn() *Manifest {
	m, err := Parse(builtIn)
	if err != nil {
		panic(err) // a broken built-in manifest is caught by the tests
	}
	return m
}

// Parse reads and checks a manifest.
func Parse(data []byte) (*Manifest, error) {
	var m Manifest
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, err
	}
	if m.Schema != 1 {
		return nil, fmt.Errorf("manifest schema %d is not supported", m.Schema)
	}
	if m.Repo == "" || m.ModAsset == "" || m.UpdaterAsset == "" {
		return nil, errors.New("manifest is missing repo, mod_asset or updater_asset")
	}
	for _, c := range m.Components {
		if c.ID == "" || c.URL == "" || len(c.SHA256) != 64 || len(c.Map) == 0 {
			return nil, fmt.Errorf("component %q is missing its id, url, sha256 or map", c.ID)
		}
		if len(c.Detect) == 0 && c.DetectName == "" {
			return nil, fmt.Errorf("component %q has no way to detect it", c.ID)
		}
	}
	return &m, nil
}

// Skipped reports whether an archive entry is one the manifest never installs.
func (m *Manifest) Skipped(name string) bool {
	ext := strings.ToLower(path.Ext(name))
	for _, s := range m.SkipExtensions {
		if ext == strings.ToLower(s) {
			return true
		}
	}
	return false
}

// Target returns where the archive entry name goes, relative to the game
// folder, or false when the component doesn't install it.
func (c Component) Target(name string) (string, bool) {
	best, bestKey := "", -1
	for from, to := range c.Map {
		if strings.HasPrefix(name, from) && len(from) > bestKey {
			best, bestKey = to+strings.TrimPrefix(name, from), len(from)
		}
	}
	return best, bestKey >= 0
}
