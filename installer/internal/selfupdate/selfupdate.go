// Package selfupdate finds GitHub releases and replaces the updater with a
// newer build published as a release asset.
//
// Two kinds of release share the repository. LethalAccess itself is tagged
// "vMAJOR.MINOR.PATCH" and carries the mod as a zip; the updater is tagged
// "updater-vMAJOR.MINOR.PATCH" and carries the updater executable. Windows
// lets a running executable be renamed but not overwritten, so the old file
// is set aside as "<name>.old" (see package swap) and removed by a later run
// once nothing uses it.
package selfupdate

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/fetch"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/swap"
)

// Tag prefixes of the two kinds of release.
const (
	ModTagPrefix     = "v"
	UpdaterTagPrefix = "updater-v"
)

// APIBase is the GitHub API. Tests point it at a local server.
var APIBase = "https://api.github.com"

// Release is the newest release of one kind.
type Release struct {
	Version    string
	Prerelease bool
	// Page is the release's page on GitHub, which holds its notes.
	Page   string
	Assets map[string]Asset
}

type Asset struct {
	URL    string
	SHA256 string
}

// Latest returns the newest release whose tag is prefix followed by a
// version, or nil when none exists. Pre-releases (Beta builds) count only
// when includePre is set.
func Latest(repo, prefix string, includePre bool) (*Release, error) {
	data, err := fetch.Bytes(fmt.Sprintf("%s/repos/%s/releases?per_page=50", APIBase, repo))
	if err != nil {
		return nil, err
	}
	var releases []struct {
		Tag        string `json:"tag_name"`
		Draft      bool   `json:"draft"`
		Prerelease bool   `json:"prerelease"`
		Page       string `json:"html_url"`
		Assets     []struct {
			Name   string `json:"name"`
			URL    string `json:"browser_download_url"`
			Digest string `json:"digest"` // "sha256:<hex>"
		} `json:"assets"`
	}
	if err := json.Unmarshal(data, &releases); err != nil {
		return nil, err
	}
	var best *Release
	for _, r := range releases {
		if r.Draft || (r.Prerelease && !includePre) || !strings.HasPrefix(r.Tag, prefix) {
			continue
		}
		v := strings.TrimPrefix(r.Tag, prefix)
		if _, ok := parse(v); !ok {
			continue
		}
		if best != nil && !Newer(v, best.Version) {
			continue
		}
		rel := &Release{Version: v, Prerelease: r.Prerelease, Page: r.Page, Assets: map[string]Asset{}}
		for _, a := range r.Assets {
			sha, _ := strings.CutPrefix(a.Digest, "sha256:")
			rel.Assets[a.Name] = Asset{URL: a.URL, SHA256: sha}
		}
		best = rel
	}
	return best, nil
}

// Newer reports whether version a is newer than b. Unparseable versions
// (such as "dev" builds) are never newer and never replaced.
func Newer(a, b string) bool {
	va, okA := parse(a)
	vb, okB := parse(b)
	if !okA || !okB {
		return false
	}
	for i := range va {
		if va[i] != vb[i] {
			return va[i] > vb[i]
		}
	}
	return false
}

func parse(v string) ([3]int, bool) {
	var out [3]int
	parts := strings.Split(v, ".")
	if len(parts) != 3 {
		return out, false
	}
	for i, p := range parts {
		n, err := strconv.Atoi(p)
		if err != nil || n < 0 {
			return out, false
		}
		out[i] = n
	}
	return out, true
}

// Replace downloads asset and swaps it in for the executable at target,
// which may be running.
func Replace(target string, asset Asset) error {
	if asset.SHA256 == "" {
		return fmt.Errorf("release asset for %s has no SHA-256 digest", filepath.Base(target))
	}
	staged := target + ".new"
	if err := fetch.File(asset.URL, staged, fetch.Expect{SHA256: asset.SHA256}); err != nil {
		return err
	}
	if _, err := swap.Into(staged, target); err != nil {
		os.Remove(staged)
		return err
	}
	return nil
}

// CleanupOld removes the copies earlier replacements set aside ("<name>.old",
// "<name>.<n>.old") and any staged "<name>.new", except ones still running.
func CleanupOld(paths ...string) {
	swap.Cleanup(paths...)
}
