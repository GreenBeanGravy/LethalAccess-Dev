package game

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestParseLibraries(t *testing.T) {
	vdf := `"libraryfolders"
{
	"0"
	{
		"path"		"C:\\Program Files (x86)\\Steam"
		"apps" { "228980" "1" }
	}
	"1"
	{
		"path"		"D:\\SteamLibrary"
	}
	"2"
	{
		"path"		"D:\\SteamLibrary\\"
	}
}`
	got := ParseLibraries(vdf)
	want := []string{`C:\Program Files (x86)\Steam`, `D:\SteamLibrary`}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("ParseLibraries = %q, want %q", got, want)
	}
}

func TestIsGameDir(t *testing.T) {
	dir := t.TempDir()
	if IsGameDir(dir) {
		t.Fatal("an empty folder counted as the game folder")
	}
	os.WriteFile(filepath.Join(dir, ExeName), nil, 0o644)
	if !IsGameDir(dir) {
		t.Fatal("a folder with the game's executable did not count")
	}
}

func TestFindFromUpdaterFolder(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, ExeName), nil, 0o644)
	sub := filepath.Join(dir, "BepInEx", "tools")
	os.MkdirAll(sub, 0o755)
	got, err := Find(sub)
	if err != nil || got != dir {
		t.Errorf("Find(%q) = %q, %v; want %q", sub, got, err, dir)
	}
}
