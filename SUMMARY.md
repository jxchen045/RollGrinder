# RollGrinder test run 20261108-154026

- verdict: **PASS**
- commit: 76604f65a386ed1b5a3ed695f1003ea6e7cf11ce
- duration: 4 min

| stage | status | detail |
|---|---|---|
| SourceCheck | PASS | no leftover files (git) |
| Build | PASS | warnings=0 |
| UnitTests | SKIP | skipped by -SkipUnitTests |
| UI/render | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 43s |
| UI/compact | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 35s |
| UI/en-US | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 36s |
| UI/dark | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 36s |

## Files
- environment.txt, build.log
- unit\<project>.log / .trx / .failed.txt
- ui\<pass>\result\selftest.log (readable), selftest.jsonl (per step), summary.json, screenshots\, files\, prints\
- ui\<pass>\app-logs\ (application log incl. stack traces)
- screenshots: policy key, max 80 per pass, layout standard; data\ databases left out (-IncludeData to keep)
