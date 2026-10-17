Fixtures for `scripts/check-deploy-compose.sh`. The repo scan prunes this directory.

`expect.txt` of `PASS` means the guard must exit 0. Any other `expect.txt` lists error text that must appear, and the guard must exit 1. At least one of those lines must exactly equal a full guard `::error file=...` line. `config failed` and `could not read` in the guard output fail the check even when `expect.txt` lists them. Defaults are busybox so a variable image is not rejected by an empty render. Security reviews changes in this directory.
