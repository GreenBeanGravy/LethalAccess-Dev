// Package game finds the Lethal Company folder and tells whether the game is
// running from it.
package game

import (
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

const (
	// AppID is Lethal Company's Steam app ID.
	AppID = "1966720"
	// ExeName is the game's executable, which marks a game folder.
	ExeName = "Lethal Company.exe"
)

// IsGameDir reports whether dir holds the game.
func IsGameDir(dir string) bool {
	info, err := os.Stat(filepath.Join(dir, ExeName))
	return err == nil && !info.IsDir()
}

// Find returns the game folder. It tries the folders around the updater
// first (someone may keep it in the game folder), then every Steam library.
func Find(updaterDir string) (string, error) {
	for dir := updaterDir; dir != ""; {
		if IsGameDir(dir) {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	for _, steam := range steamRoots() {
		for _, lib := range libraries(steam) {
			if dir := inLibrary(lib); dir != "" {
				return dir, nil
			}
		}
	}
	return "", errors.New("Lethal Company was not found in any Steam library")
}

// steamRoots returns the Steam install folders named in the registry.
func steamRoots() []string {
	var roots []string
	keys := []struct {
		root  registry.Key
		path  string
		value string
	}{
		{registry.CURRENT_USER, `Software\Valve\Steam`, "SteamPath"},
		{registry.LOCAL_MACHINE, `SOFTWARE\WOW6432Node\Valve\Steam`, "InstallPath"},
		{registry.LOCAL_MACHINE, `SOFTWARE\Valve\Steam`, "InstallPath"},
	}
	for _, k := range keys {
		key, err := registry.OpenKey(k.root, k.path, registry.QUERY_VALUE)
		if err != nil {
			continue
		}
		v, _, err := key.GetStringValue(k.value)
		key.Close()
		if err == nil && v != "" {
			roots = appendUnique(roots, filepath.Clean(filepath.FromSlash(v)))
		}
	}
	return roots
}

var libraryPath = regexp.MustCompile(`"path"\s+"([^"]+)"`)

// libraries returns the Steam library folders listed in steam's
// libraryfolders.vdf, always including steam itself.
func libraries(steam string) []string {
	libs := []string{steam}
	data, err := os.ReadFile(filepath.Join(steam, "steamapps", "libraryfolders.vdf"))
	if err != nil {
		return libs
	}
	return appendUnique(libs, ParseLibraries(string(data))...)
}

// ParseLibraries reads the library paths out of a libraryfolders.vdf.
func ParseLibraries(vdf string) []string {
	var out []string
	for _, m := range libraryPath.FindAllStringSubmatch(vdf, -1) {
		out = appendUnique(out, filepath.Clean(strings.ReplaceAll(m[1], `\\`, `\`)))
	}
	return out
}

var installDir = regexp.MustCompile(`"installdir"\s+"([^"]+)"`)

// inLibrary returns the game folder in the Steam library lib, or "".
func inLibrary(lib string) string {
	name := "Lethal Company"
	if data, err := os.ReadFile(filepath.Join(lib, "steamapps", "appmanifest_"+AppID+".acf")); err == nil {
		if m := installDir.FindStringSubmatch(string(data)); m != nil {
			name = m[1]
		}
	}
	dir := filepath.Join(lib, "steamapps", "common", name)
	if IsGameDir(dir) {
		return dir
	}
	return ""
}

func appendUnique(list []string, items ...string) []string {
	for _, item := range items {
		found := false
		for _, have := range list {
			if strings.EqualFold(have, item) {
				found = true
				break
			}
		}
		if !found {
			list = append(list, item)
		}
	}
	return list
}

// Running reports whether the game in dir is running.
func Running(dir string) bool {
	want := strings.ToLower(filepath.Join(dir, ExeName))
	snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return false
	}
	defer windows.CloseHandle(snap)
	var entry windows.ProcessEntry32
	entry.Size = uint32(unsafe.Sizeof(entry))
	for err = windows.Process32First(snap, &entry); err == nil; err = windows.Process32Next(snap, &entry) {
		if !strings.EqualFold(windows.UTF16ToString(entry.ExeFile[:]), ExeName) {
			continue
		}
		p, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.ProcessID)
		if err != nil {
			// A copy we can't look into still counts: better to wait than to
			// replace files it has open.
			return true
		}
		buf := make([]uint16, windows.MAX_LONG_PATH)
		size := uint32(len(buf))
		err = windows.QueryFullProcessImageName(p, 0, &buf[0], &size)
		windows.CloseHandle(p)
		if err != nil || strings.ToLower(windows.UTF16ToString(buf[:size])) == want {
			return true
		}
	}
	return false
}

// Launch starts the game through Steam.
func Launch() error {
	verb, _ := windows.UTF16PtrFromString("open")
	url, _ := windows.UTF16PtrFromString("steam://rungameid/" + AppID)
	return windows.ShellExecute(0, verb, url, nil, nil, windows.SW_SHOWNORMAL)
}
