Fixtures for `scripts/check-deploy-compose.sh`. The repo scan prunes this directory.

`expect.txt` of `PASS` means the guard must exit 0. Any other `expect.txt` lists error text that must appear, and the guard must exit 1. Defaults are busybox so a variable image is not rejected by an empty render.
