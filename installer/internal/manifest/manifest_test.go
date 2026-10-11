package manifest

import "testing"

func TestBuiltInParses(t *testing.T) {
	m := BuiltIn()
	if len(m.Components) == 0 {
		t.Fatal("the built-in manifest lists no components")
	}
}

func TestSkipped(t *testing.T) {
	m := BuiltIn()
	for name, want := range map[string]bool{
		"README.md":                    true,
		"plugins/X/CHANGELOG.MD":       true,
		"BepInEx/plugins/X/mod.dll":    false,
		"BepInEx/plugins/X/readme.txt": false,
	} {
		if got := m.Skipped(name); got != want {
			t.Errorf("Skipped(%q) = %v, want %v", name, got, want)
		}
	}
}

func TestTarget(t *testing.T) {
	c := Component{Map: map[string]string{"plugins/": "BepInEx/plugins/", "plugins/skip/": "elsewhere/"}}
	cases := []struct {
		in, want string
		ok       bool
	}{
		{"plugins/A/a.dll", "BepInEx/plugins/A/a.dll", true},
		{"plugins/skip/b.dll", "elsewhere/b.dll", true},
		{"icon.png", "", false},
	}
	for _, tc := range cases {
		got, ok := c.Target(tc.in)
		if got != tc.want || ok != tc.ok {
			t.Errorf("Target(%q) = %q, %v; want %q, %v", tc.in, got, ok, tc.want, tc.ok)
		}
	}
	all := Component{Map: map[string]string{"": ""}}
	if got, ok := all.Target("BepInEx/core/BepInEx.dll"); !ok || got != "BepInEx/core/BepInEx.dll" {
		t.Errorf("an empty map key should install everything as is, got %q, %v", got, ok)
	}
}

func TestParseRejects(t *testing.T) {
	bad := []string{
		`{"schema":2,"repo":"a/b","mod_asset":"m","updater_asset":"u"}`,
		`{"schema":1,"repo":"","mod_asset":"m","updater_asset":"u"}`,
		`{"schema":1,"repo":"a/b","mod_asset":"m","updater_asset":"u","components":[{"id":"x","url":"u","sha256":"short","map":{"":""},"detect":["a"]}]}`,
		`{"schema":1,"repo":"a/b","mod_asset":"m","updater_asset":"u","components":[{"id":"x","url":"u","sha256":"0000000000000000000000000000000000000000000000000000000000000000","map":{"":""}}]}`,
	}
	for _, s := range bad {
		if _, err := Parse([]byte(s)); err == nil {
			t.Errorf("Parse accepted %s", s)
		}
	}
}
