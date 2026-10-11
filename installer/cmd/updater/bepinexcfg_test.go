package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestFixHideManager(t *testing.T) {
	for _, tc := range []struct {
		name, in, want string
		changed        bool
	}{
		{
			name:    "false becomes true",
			in:      "[Chainloader]\r\n\r\n## Hide\r\n# Setting type: Boolean\r\nHideManagerGameObject = false\r\n\r\n[Logging]\r\nX = 1\r\n",
			want:    "[Chainloader]\r\n\r\n## Hide\r\n# Setting type: Boolean\r\nHideManagerGameObject = true\r\n\r\n[Logging]\r\nX = 1\r\n",
			changed: true,
		},
		{
			name: "already true",
			in:   "[Chainloader]\nHideManagerGameObject = true\n",
			want: "[Chainloader]\nHideManagerGameObject = true\n",
		},
		{
			name: "true in any case and spacing",
			in:   "[Chainloader]\nHideManagerGameObject=True\n",
			want: "[Chainloader]\nHideManagerGameObject=True\n",
		},
		{
			name:    "key missing from the section",
			in:      "[Chainloader]\nLogUnityMessages = true\n\n[Logging]\nX = 1\n",
			want:    "[Chainloader]\nHideManagerGameObject = true\nLogUnityMessages = true\n\n[Logging]\nX = 1\n",
			changed: true,
		},
		{
			name:    "same key in another section is left alone",
			in:      "[Other]\nHideManagerGameObject = false\n",
			want:    "[Other]\nHideManagerGameObject = false\n\n[Chainloader]\nHideManagerGameObject = true\n",
			changed: true,
		},
		{
			name:    "commented-out key does not count",
			in:      "[Chainloader]\n# HideManagerGameObject = true\n",
			want:    "[Chainloader]\nHideManagerGameObject = true\n# HideManagerGameObject = true\n",
			changed: true,
		},
		{
			name:    "empty file",
			in:      "",
			want:    "[Chainloader]\r\nHideManagerGameObject = true\r\n",
			changed: true,
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, changed := fixHideManager(tc.in)
			if got != tc.want || changed != tc.changed {
				t.Errorf("got %q, %v\nwant %q, %v", got, changed, tc.want, tc.changed)
			}
		})
	}
}

func TestFixBepInExConfig(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, filepath.FromSlash(bepinexConfig))

	// No config yet: BepInEx writes the rest of it on the first launch.
	if changed, err := fixBepInExConfig(dir); err != nil || !changed {
		t.Fatalf("missing config: changed %v, err %v", changed, err)
	}
	if !bepinexConfigOK(dir) {
		t.Fatal("config still reported broken after the fix")
	}
	if changed, err := fixBepInExConfig(dir); err != nil || changed {
		t.Fatalf("second run: changed %v, err %v", changed, err)
	}

	write(t, dir, bepinexConfig, "[Chainloader]\r\nHideManagerGameObject = false\r\n")
	if bepinexConfigOK(dir) {
		t.Fatal("false reported as fine")
	}
	if changed, err := fixBepInExConfig(dir); err != nil || !changed {
		t.Fatalf("broken config: changed %v, err %v", changed, err)
	}
	b, _ := os.ReadFile(p)
	if string(b) != "[Chainloader]\r\nHideManagerGameObject = true\r\n" {
		t.Fatalf("wrote %q", b)
	}
}
