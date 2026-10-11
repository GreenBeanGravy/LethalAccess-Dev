// Package install copies files out of a zip into the game folder.
//
// A file is only written when its contents changed, and a file the game has
// open is set aside (see package swap) instead of blocking the update. Paths
// in an archive are checked so that none can land outside the game folder.
package install

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"

	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/swap"
)

// Progress, when set, is told how many files of the total are done.
var Progress func(done, total int)

// Entry is one file to install: where it comes from in the archive and
// where it goes, relative to the game folder with forward slashes.
type Entry struct {
	file   *zip.File
	Target string
}

// Plan lists the files of the archive at zipPath that target accepts.
// target returns where an archive path goes, or false to skip it.
func Plan(r *zip.Reader, target func(name string) (string, bool)) ([]Entry, error) {
	var out []Entry
	seen := map[string]bool{}
	for _, f := range r.File {
		if f.FileInfo().IsDir() || strings.HasSuffix(f.Name, "/") {
			continue
		}
		name := strings.ReplaceAll(f.Name, `\`, "/")
		dest, ok := target(name)
		if !ok {
			continue
		}
		clean, err := Clean(dest)
		if err != nil {
			return nil, fmt.Errorf("the archive entry %q: %w", f.Name, err)
		}
		key := strings.ToLower(clean)
		if seen[key] {
			return nil, fmt.Errorf("the archive has %q twice", clean)
		}
		seen[key] = true
		out = append(out, Entry{file: f, Target: clean})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Target < out[j].Target })
	return out, nil
}

// Clean checks a game folder relative path and returns it in canonical form.
// It refuses absolute paths and any path that climbs out with "..".
func Clean(rel string) (string, error) {
	rel = strings.ReplaceAll(rel, `\`, "/")
	if rel == "" || strings.HasPrefix(rel, "/") || filepath.VolumeName(filepath.FromSlash(rel)) != "" || strings.Contains(rel, ":") {
		return "", errors.New("is not a relative path")
	}
	c := path.Clean(rel)
	if c == "." || c == ".." || strings.HasPrefix(c, "../") {
		return "", errors.New("points outside the game folder")
	}
	return c, nil
}

// Result says what Apply changed.
type Result struct {
	// Files maps every planned file to its SHA-256.
	Files    map[string]string
	Updated  []string
	SetAside []string
}

// Apply installs entries into gameDir, writing only files whose contents
// differ from what is there.
func Apply(gameDir string, entries []Entry) (Result, error) {
	res := Result{Files: map[string]string{}}
	for i, e := range entries {
		if Progress != nil {
			Progress(i, len(entries))
		}
		dest := filepath.Join(gameDir, filepath.FromSlash(e.Target))
		data, err := read(e.file)
		if err != nil {
			return res, fmt.Errorf("reading %s from the download: %w", e.Target, err)
		}
		sum := sha256.Sum256(data)
		hash := hex.EncodeToString(sum[:])
		res.Files[e.Target] = hash
		if have, err := HashFile(dest); err == nil && have == hash {
			continue
		}
		if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
			return res, err
		}
		staged := dest + ".new"
		if err := os.WriteFile(staged, data, 0o644); err != nil {
			return res, err
		}
		aside, err := swap.Into(staged, dest)
		if err != nil {
			os.Remove(staged)
			return res, fmt.Errorf("replacing %s: %w", e.Target, err)
		}
		if aside != "" {
			rel, _ := filepath.Rel(gameDir, aside)
			res.SetAside = append(res.SetAside, filepath.ToSlash(rel))
		}
		res.Updated = append(res.Updated, e.Target)
	}
	if Progress != nil {
		Progress(len(entries), len(entries))
	}
	return res, nil
}

func read(f *zip.File) ([]byte, error) {
	rc, err := f.Open()
	if err != nil {
		return nil, err
	}
	defer rc.Close()
	return io.ReadAll(io.LimitReader(rc, 512<<20))
}

// HashFile returns the SHA-256 of a file on disk.
func HashFile(p string) (string, error) {
	f, err := os.Open(p)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// Remove deletes the game folder relative files, setting aside any that are
// in use, and then any folders they leave empty. It returns what it removed
// and what it had to set aside.
func Remove(gameDir string, rels []string) (removed, setAside []string) {
	dirs := map[string]bool{}
	for _, rel := range rels {
		p := filepath.Join(gameDir, filepath.FromSlash(rel))
		err := os.Remove(p)
		switch {
		case err == nil:
			removed = append(removed, rel)
		case errors.Is(err, fs.ErrNotExist):
		default:
			if aside, err := swap.SetAside(p); err == nil {
				r, _ := filepath.Rel(gameDir, aside)
				setAside = append(setAside, filepath.ToSlash(r))
				removed = append(removed, rel)
			}
		}
		for d := path.Dir(rel); d != "." && d != "/"; d = path.Dir(d) {
			dirs[d] = true
		}
	}
	// Deepest first, so a parent is tried after its children.
	list := make([]string, 0, len(dirs))
	for d := range dirs {
		list = append(list, d)
	}
	sort.Slice(list, func(i, j int) bool { return strings.Count(list[i], "/") > strings.Count(list[j], "/") })
	for _, d := range list {
		os.Remove(filepath.Join(gameDir, filepath.FromSlash(d))) // fails unless empty
	}
	return removed, setAside
}

// DeleteSetAside removes files earlier runs set aside and returns the ones
// still in use.
func DeleteSetAside(gameDir string, rels []string) (left []string) {
	for _, rel := range rels {
		err := os.Remove(filepath.Join(gameDir, filepath.FromSlash(rel)))
		if err != nil && !errors.Is(err, fs.ErrNotExist) {
			left = append(left, rel)
		}
	}
	return left
}

// FindByName returns every file called name under root, game folder
// relative. Matching ignores case, as Windows does.
func FindByName(gameDir, root, name string) []string {
	var out []string
	filepath.WalkDir(filepath.Join(gameDir, filepath.FromSlash(root)), func(p string, d fs.DirEntry, err error) error {
		if err == nil && !d.IsDir() && strings.EqualFold(d.Name(), name) {
			if rel, err := filepath.Rel(gameDir, p); err == nil {
				out = append(out, filepath.ToSlash(rel))
			}
		}
		return nil
	})
	return out
}
