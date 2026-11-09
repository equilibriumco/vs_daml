# Captured dpm output

Real output of dpm 1.0.22 with SDK 3.6.0 (damlc 3.6.2) on Windows, captured on 2026-10-11 from
temporary copies of the samples, so no build output landed in the repository. Each command ran
through `%APPDATA%\dpm\bin\dpm.cmd` with stdout and stderr captured separately. The files keep
the original CRLF line endings and timestamps.

| File | Command | Working folder | Exit code |
|---|---|---|---|
| `build-broken.stderr.txt` | `dpm build` | copy of `samples/broken` | 1 |
| `build-single-package.stderr.txt` | `dpm build` | copy of `samples/single-package` | 0 |
| `build-multi-package-broken.stderr.txt` | `dpm build --all` | copy of `samples/multi-package` with `samples/broken` added as a fourth package | 1 |
| `test-broken.stderr.txt` | `dpm test` | copy of `samples/broken` | 1 |
| `test-single-package.stdout.txt`, `test-single-package.stderr.txt` | `dpm test` | copy of `samples/single-package` | 0 |
| `clean-single-package.stderr.txt` | `dpm clean` | copy of `samples/single-package` | 0 |
| `test-failing-script.stdout.txt`, `test-failing-script.stderr.txt` | `dpm test` | copy of `samples/single-package` with a `failing` script (`assertMsg "the total is wrong" False`) appended to `Main.daml`; captured on 2026-10-16 | 1 |

Files with `-3.7.1` in the name are the same commands on dpm 1.1.1 with SDK 3.7.1, captured on 2026-11-09 with
`ProcessStartInfo` (stdout and stderr apart). dpm 1.1 prints the full path after `File:`, on the next line, indented,
when it is long (`build-broken-long-path-3.7.1.stderr.txt`, built under a deep temporary folder, kept as printed apart
from the user name and a fixed folder id); it adds the script name to the source (`Source:   Script: failing`), and
its test summary counts per package and per file and names only the failed scripts. `test-single-package-3.7.1.stdout.txt`
is a passing `dpm test` of a copy of `samples/single-package`.

`dpm build` and `dpm clean` printed nothing on stdout; `dpm test` printed only its summary there. damlc colours message text with ANSI escape sequences (e.g. `ESC[0;91m` … `ESC[0m`) even though its output was redirected; they are kept here as captured.

Absolute paths were redacted: the temporary root became `C:/work` (or `C:\work`), and the user
profile in a JDK warning became `C:/Users/<user>/`. Nothing else was changed.
