# Script results fixtures

The HTML `damlc` sends in `daml/virtualResource/didChange` for the `setup` script of
`samples/single-package/daml/Main.daml`. The only edit is the project path in the source links,
replaced with `C:/work/single-package`; the pages carry no other paths or user data and still have
the `$webviewSrc`/`$webviewCss` placeholders the host replaces.

- `sdk-3.5.2-setup.html` — SDK 3.5.2.
- `sdk-3.6.0-setup.html` — SDK 3.6.0, `damlc multi-ide`.

Both must keep fitting the pinned `third_party/daml-extension/webview.js`; a new SDK's page goes
here too, so the format check runs against it.
