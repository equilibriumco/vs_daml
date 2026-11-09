# Script results fixtures

The HTML `damlc` sends in `daml/virtualResource/didChange` for the `setup` script of
`samples/single-package/daml/Main.daml`. The only edit is the project path in the source links,
replaced with `C:/work/single-package`; the pages carry no other paths or user data and still have
the `$webviewSrc`/`$webviewCss` placeholders the host replaces.

- `sdk-3.7.1-setup.html` — SDK 3.7.1, `damlc multi-ide`.
- `sdk-3.6.4-setup.html` — SDK 3.6.4, `damlc multi-ide`.

Both must keep fitting the pinned `third_party/daml-extension/webview.js`; a new SDK's page goes
here too, so the format check runs against it. `DamlcContractTests` writes the page it receives
to `artifacts/test/script-results` when `EQDAML_RECORD_FIXTURES=1` is set; copy it here after a look.
