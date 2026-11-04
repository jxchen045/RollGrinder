# RollGrinder test run 20261104-131456

- verdict: **PASS**
- commit: 176e2bbf86f67909143e0c05ba5974888fa16f70
- duration: 3.5 min

| stage | status | detail |
|---|---|---|
| SourceCheck | PASS | no leftover files (git) |
| Build | PASS | warnings=0 |
| UnitTests | SKIP | skipped by -SkipUnitTests |
| UI/render | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 45s |
| UI/compact | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 37s |
| UI/en-US | PASS | exit=0 steps=70 pass=70 warn=0 fail=0 skip=0 39s |

## Files
- environment.txt, build.log
- unit\<project>.log / .trx / .failed.txt
- ui\<pass>\result\selftest.log (readable), selftest.jsonl (per step), summary.json, screenshots\, files\, prints\
- ui\<pass>\app-logs\ (application log incl. stack traces)
- screenshots: policy key, max 80 per pass, layout standard; data\ databases left out (-IncludeData to keep)
