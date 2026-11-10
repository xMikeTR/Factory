# Split fixtures

`Fares.Api` and `Fares.Portal` are `System2/fares-sample` split in two, the way a split run sees them
(`--repo Api=... --repo Ui=...`). Tests copy each folder into a temp directory and `git init` it; nothing here is built.
The original sample is untouched.
