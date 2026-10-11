package install

import (
	"archive/zip"
	"bytes"
	"os"
	"path/filepath"
	"slices"
	"testing"
)

func TestClean(t *testing.T) {
	good := map[string]string{
		"a/b.dll":        "a/b.dll",
		`a\b.dll`:        "a/b.dll",
		"a/./b/../c.wav": "a/c.wav",
	}
	for in, want := range good {
		if got, err := Clean(in); err != nil || got != want {
			t.Errorf("Clean(%q) = %q, %v; want %q", in, got, err, want)
		}
	}
	for _, in := range []string{"", "/abs", `C:\x`, "C:x", "..", "../x", "a/../../x", "."} {
		if got, err := Clean(in); err == nil {
			t.Errorf("Clean(%q) = %q, want an error", in, got)
		}
	}
}

func makeZip(t *testing.T, files map[string]string) *zip.Reader {
	t.Helper()
	var buf bytes.Buffer
	w := zip.NewWriter(&buf)
	for name, body := range files {
		f, err := w.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		f.Write([]byte(body))
	}
	w.Close()
	r, err := zip.NewReader(bytes.NewReader(buf.Bytes()), int64(buf.Len()))
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func all(name string) (string, bool) { return name, true }

func TestPlanRefusesEscapes(t *testing.T) {
	r := makeZip(t, map[string]string{"../evil.dll": "x"})
	if _, err := Plan(r, all); err == nil {
		t.Fatal("Plan accepted a path outside the game folder")
	}
	r = makeZip(t, map[string]string{"A/x.dll": "1", "a/X.dll": "2"})
	if _, err := Plan(r, all); err == nil {
		t.Fatal("Plan accepted two entries that are one file on Windows")
	}
}

func TestApplyAndRemove(t *testing.T) {
	dir := t.TempDir()
	r := makeZip(t, map[string]string{"P/a.dll": "one", "P/sub/b.wav": "two", "skip.md": "no"})
	entries, err := Plan(r, func(n string) (string, bool) { return n, filepath.Ext(n) != ".md" })
	if err != nil {
		t.Fatal(err)
	}
	res, err := Apply(dir, entries)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Files) != 2 || len(res.Updated) != 2 {
		t.Fatalf("first apply: files %v updated %v", res.Files, res.Updated)
	}
	if _, err := os.Stat(filepath.Join(dir, "skip.md")); err == nil {
		t.Fatal("a skipped entry was installed")
	}

	// The same archive again changes nothing.
	res, err = Apply(dir, entries)
	if err != nil || len(res.Updated) != 0 {
		t.Fatalf("second apply updated %v, %v", res.Updated, err)
	}

	// A changed file is rewritten.
	r = makeZip(t, map[string]string{"P/a.dll": "ONE"})
	entries, _ = Plan(r, all)
	res, err = Apply(dir, entries)
	if err != nil || !slices.Equal(res.Updated, []string{"P/a.dll"}) {
		t.Fatalf("changed apply updated %v, %v", res.Updated, err)
	}
	if data, _ := os.ReadFile(filepath.Join(dir, "P", "a.dll")); string(data) != "ONE" {
		t.Fatalf("P/a.dll holds %q", data)
	}

	removed, aside := Remove(dir, []string{"P/sub/b.wav", "P/missing.dll"})
	if !slices.Equal(removed, []string{"P/sub/b.wav"}) || len(aside) != 0 {
		t.Fatalf("Remove = %v, %v", removed, aside)
	}
	if _, err := os.Stat(filepath.Join(dir, "P", "sub")); err == nil {
		t.Fatal("the emptied folder was left behind")
	}
	if _, err := os.Stat(filepath.Join(dir, "P")); err != nil {
		t.Fatal("a folder that still holds files was removed")
	}
}

func TestFindByName(t *testing.T) {
	dir := t.TempDir()
	for _, p := range []string{"BepInEx/plugins/A/Mod.dll", "BepInEx/plugins/B/C/mod.DLL", "BepInEx/core/mod.dll"} {
		full := filepath.Join(dir, filepath.FromSlash(p))
		os.MkdirAll(filepath.Dir(full), 0o755)
		os.WriteFile(full, nil, 0o644)
	}
	got := FindByName(dir, "BepInEx/plugins", "mod.dll")
	slices.Sort(got)
	want := []string{"BepInEx/plugins/A/Mod.dll", "BepInEx/plugins/B/C/mod.DLL"}
	if !slices.Equal(got, want) {
		t.Errorf("FindByName = %v, want %v", got, want)
	}
}
