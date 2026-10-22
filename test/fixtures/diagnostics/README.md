# Diagnostics fixtures

`sensitive.log` is a made-up log file in the extension's log format. Every line plants something
that must not survive a diagnostic export: the user and machine name, project folders in every
path spelling (drive, slashes, `file:` URI with an escaped colon, UNC), source code from compiler
messages and protocol messages, script results (assertion text, a partial transaction, party and
contract IDs) and credentials (URL user info, a bearer token, a password variable, GitHub and AWS
keys, an e-mail address). The values are fake; the GitHub and AWS keys follow the public example
formats. `DiagnosticReportTests` checks that none of them reaches the report, and that the parts a
bug report needs (levels, categories, codes such as `EQD0101`, file names, tool versions) do.
