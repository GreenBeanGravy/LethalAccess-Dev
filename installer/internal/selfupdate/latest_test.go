package selfupdate

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

const releasesJSON = `[
 {"tag_name":"updater-v9.0.0","html_url":"u","assets":[]},
 {"tag_name":"v1.2.0","prerelease":true,"html_url":"p120","assets":[{"name":"LethalAccess.zip","browser_download_url":"x","digest":"sha256:abc"}]},
 {"tag_name":"v1.1.0","html_url":"p110","assets":[{"name":"LethalAccess.zip","browser_download_url":"y","digest":"sha256:def"}]},
 {"tag_name":"v1.3.0","draft":true,"assets":[]},
 {"tag_name":"v1.0.0","html_url":"p100","assets":[]},
 {"tag_name":"vbad","assets":[]}
]`

func TestLatest(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/repos/o/r/releases" {
			http.NotFound(w, r)
			return
		}
		w.Write([]byte(releasesJSON))
	}))
	defer srv.Close()
	old := APIBase
	APIBase = srv.URL
	defer func() { APIBase = old }()

	rel, err := Latest("o/r", ModTagPrefix, false)
	if err != nil || rel == nil || rel.Version != "1.1.0" || rel.Page != "p110" || rel.Assets["LethalAccess.zip"].SHA256 != "def" {
		t.Fatalf("stable: %+v, %v", rel, err)
	}
	rel, err = Latest("o/r", ModTagPrefix, true)
	if err != nil || rel == nil || rel.Version != "1.2.0" || !rel.Prerelease {
		t.Fatalf("beta: %+v, %v", rel, err)
	}
	rel, err = Latest("o/r", UpdaterTagPrefix, false)
	if err != nil || rel == nil || rel.Version != "9.0.0" {
		t.Fatalf("updater: %+v, %v", rel, err)
	}
	if rel, err = Latest("o/none", ModTagPrefix, false); err == nil {
		t.Fatalf("a missing repository returned %+v", rel)
	}
}
