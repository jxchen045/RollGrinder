# RollGrinder test run 20261008-155957

- verdict: **PASS**
- commit: 12df65a0370f55d3f837a86b74bd0de92b523aaf
- duration: 8.5 min

| stage | status | detail |
|---|---|---|
| SourceCheck | PASS | no leftover files (git) |
| Build | PASS | warnings=0 |
| UnitTests | SKIP | skipped by -SkipUnitTests |
| UI/sim | PASS | exit=0 steps=309 pass=287 warn=0 fail=0 skip=22 326s |
| UI/render | PASS | exit=0 steps=67 pass=67 warn=0 fail=0 skip=0 36s |
| UI/compact | PASS | exit=0 steps=67 pass=67 warn=0 fail=0 skip=0 36s |

## Files
- environment.txt, build.log
- unit\<project>.log / .trx / .failed.txt
- ui\<pass>\result\selftest.log (readable), selftest.jsonl (per step), summary.json, screenshots\, files\, prints\
- ui\<pass>\app-logs\ (application log incl. stack traces)
- screenshots: policy key, max 80 per pass, layout compact; data\ databases left out (-IncludeData to keep)
