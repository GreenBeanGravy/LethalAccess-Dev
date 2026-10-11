package main

import (
	"archive/zip"
	"os"
	"path/filepath"
	"slices"
	"testing"

	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/install"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/manifest"
	"github.com/GreenBeanGravy/LethalAccess-Dev/installer/internal/state"
)

func write(t *testing.T, dir, rel, body string) {
	t.Helper()
	p := filepath.Join(dir, filepath.FromSlash(rel))
	os.MkdirAll(filepath.Dir(p), 0o755)
	if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestModTarget(t *testing.T) {
	target := modTarget(manifest.BuiltIn())
	for name, want := range map[string]bool{
		"VERSION": false,
		"BepInEx/plugins/LethalAccess/LethalAccess Remake.dll": true,
		"BepInEx/plugins/LethalAccess/README.md":               false,
		"winhttp.dll":                                          false,
		"accesskit.dll":                                        true,
		"UnityPlayer.dll":                                      false,
		"sub/accesskit.dll":                                    false,
	} {
		if _, ok := target(name); ok != want {
			t.Errorf("modTarget(%q) = %v, want %v", name, ok, want)
		}
	}
}

func TestZipVersion(t *testing.T) {
	p := filepath.Join(t.TempDir(), "m.zip")
	f, _ := os.Create(p)
	w := zip.NewWriter(f)
	v, _ := w.Create("VERSION")
	v.Write([]byte("1.2.3\r\n"))
	w.Close()
	f.Close()
	if got, err := zipVersion(p); err != nil || got != "1.2.3" {
		t.Fatalf("zipVersion = %q, %v", got, err)
	}
}

func TestDisableStrays(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "BepInEx/plugins/LethalAccess/UnityA11y.dll", "new")
	write(t, dir, "BepInEx/plugins/Old/UnityA11y.dll", "old")
	write(t, dir, "BepInEx/plugins/Old/sound.wav", "keep")
	got := disableStrays(dir, map[string]string{
		"BepInEx/plugins/LethalAccess/UnityA11y.dll": "",
		"BepInEx/plugins/LethalAccess/a.wav":         "",
	})
	if !slices.Equal(got, []string{"BepInEx/plugins/Old/UnityA11y.dll"}) {
		t.Fatalf("disableStrays = %v", got)
	}
	if _, err := os.Stat(filepath.Join(dir, "BepInEx/plugins/Old/UnityA11y.dll.disabled")); err != nil {
		t.Fatal("the stray copy was not renamed")
	}
	if _, err := os.Stat(filepath.Join(dir, "BepInEx/plugins/LethalAccess/UnityA11y.dll")); err != nil {
		t.Fatal("the installed copy was touched")
	}
}

func TestIsHealthy(t *testing.T) {
	dir := t.TempDir()
	m := manifest.BuiltIn()
	write(t, dir, "BepInEx/plugins/LethalAccess/a.dll", "x")
	sum, _ := install.HashFile(filepath.Join(dir, "BepInEx/plugins/LethalAccess/a.dll"))
	st := &state.State{Files: map[string]string{"BepInEx/plugins/LethalAccess/a.dll": sum}}

	if !isHealthy(dir, m, st, true) {
		t.Fatal("a matching install without components was not healthy")
	}
	if isHealthy(dir, m, st, false) {
		t.Fatal("an install missing BepInEx was healthy")
	}
	for _, rel := range []string{"BepInEx/core/BepInEx.dll", "winhttp.dll", "doorstop_config.ini"} {
		write(t, dir, rel, "")
	}
	write(t, dir, bepinexConfig, "[Chainloader]\nHideManagerGameObject = true\n")
	if isHealthy(dir, m, st, false) {
		t.Fatal("an install missing InputUtils, never declined, was healthy")
	}
	st.SetDeclined("inpututils", true)
	if !isHealthy(dir, m, st, false) {
		t.Fatal("a declined optional component made the install unhealthy")
	}
	write(t, dir, bepinexConfig, "[Chainloader]\nHideManagerGameObject = false\n")
	if isHealthy(dir, m, st, false) {
		t.Fatal("a BepInEx.cfg that unloads the mods was healthy")
	}
	if !isHealthy(dir, m, st, true) {
		t.Fatal("--no-components looked at BepInEx.cfg")
	}
	write(t, dir, bepinexConfig, "[Chainloader]\nHideManagerGameObject = true\n")
	st.SetDeclined("inpututils", false)
	write(t, dir, "BepInEx/plugins/Other Mods/LethalCompanyInputUtils/LethalCompanyInputUtils.dll", "")
	if !isHealthy(dir, m, st, false) {
		t.Fatal("InputUtils in a different folder was not detected")
	}
	write(t, dir, "BepInEx/plugins/LethalAccess/a.dll", "changed")
	if isHealthy(dir, m, st, false) {
		t.Fatal("a changed file was healthy")
	}
}
