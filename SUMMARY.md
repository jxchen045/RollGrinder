# RollGrinder test run 20261015-093538

- verdict: **PASS**
- commit: 4f1eadd8f57ff0187a89b54708bc40be240cef4b
- duration: 7.4 min

| stage | status | detail |
|---|---|---|
| SourceCheck | PASS | no leftover files (git) |
| Build | PASS | warnings=0 |
| UnitTests | SKIP | skipped by -SkipUnitTests |
| UI/sim | PASS | exit=0 steps=309 pass=285 warn=0 fail=0 skip=24 309s |
| UI/render | PASS | exit=0 steps=67 pass=67 warn=0 fail=0 skip=0 35s |
| UI/compact | PASS | exit=0 steps=67 pass=67 warn=0 fail=0 skip=0 33s |

## Files
- environment.txt, build.log
- unit\<project>.log / .trx / .failed.txt
- ui\<pass>\result\selftest.log (readable), selftest.jsonl (per step), summary.json, screenshots\, files\, prints\
- ui\<pass>\app-logs\ (application log incl. stack traces)
- screenshots: policy key, max 80 per pass, layout standard; data\ databases left out (-IncludeData to keep)
