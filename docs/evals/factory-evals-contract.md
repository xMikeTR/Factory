# Recording eval runs so designc can compare them

This guide is for whoever builds the eval harness for Build (the coding pipeline). designc reads what the harness records, shows it in Improve, and never runs the harness. The method behind it is in the Evaluation Bible; this file is only the wire contract.

## Layout: the factory registry

A factory registry is a folder with a `designc.json` and no model. It can point at design registries with `"registries": {"design": "../design-registry"}`; the pointer goes one way and designc only reads the other folder (escapes appear in Improve).

| Path | Written by | Holds |
| --- | --- | --- |
| `factory/tasks/<id>.yaml` | Human, or the Analyst agent as `minted` | One eval task. `source.failure_mode` names the mode it came from. `status` is `minted`, `checked` or `retired` |
| `factory/suites/*.yaml` | Human | Task ids and run settings. The harness owns its shape; designc does not read it |
| `factory/rubrics/<dimension>.md` | Human | One judge prompt per dimension |
| `factory/labels/<dimension>.yaml` | Human | Judge calibration rounds: `rounds: [{date, judge_version, precision, recall, n}]` |
| `factory/taxonomy.yaml` | Human | `modes: [{id, name, triage (spec, fix or grader), description?}]` |
| `ledger/factory.jsonl` | `designc record` and the app | The events below |

`evals/` (agent evals, slice 15) is a different thing and is untouched.

## What the harness writes

Pipe JSON lines into `designc record <registry> --by <name>`. It accepts exactly three kinds, fixed in `python/designc/factory.py`, and a batch is all or nothing (exit 0 recorded, 1 nothing recorded with `line N: ...` on stderr, 2 no such folder). It needs no model and no settings.

| Kind | Fields | When |
| --- | --- | --- |
| `suite_run` | `suite_run_id, suite, harness_version, model, prompt_version, trials, tasks {<task_id>: {hash, gates[], gradients[]?}}`, `configurator?` | Once, at the start. `hash` is the hash of the task file as the run used it (`Core.evalHash`: `hash16` of the text with CRLF as LF). `gates` decide a trial; `gradients` are shown only |
| `grader` | `suite_run_id, task_id, trial, grader, verdict (pass, fail, unknown, skipped)`, `reason?, ms?, judge_version?, rubric_hash?, trace_id?` | One per grader per trial. `reason` helps on a fail; `trace_id` links to Langfuse |
| `suite_run_finished` | `suite_run_id, outcome (completed, aborted)`, `reason?` (required when aborted) | Once, at the end |

Rules designc applies when reading: a trial fails if any pinned gate failed and passes if every gate passed; otherwise it is `incomplete` while the run is open and `harness` once finished (left out of pass@1). The latest event per grader wins. Events naming an undeclared task, trial or grader are counted as stray. pass@1 is the share of judged trials that passed; pass^k is pass@1 to the power `k` (`designc.json` `factory.k`, default 3).

Reruns are a field, not an event: a `task` event carries `reruns`.

## What only the app writes

`failure_mode_classified` (`registry, escape, failure_mode`, null clears) and `factory_task_bound` (`task_id, hash`) record human decisions and are refused by `designc record`. Binding pins the task file's hash; a suite run that pinned another hash is flagged "used a different definition", and editing a bound task makes it `changed` until it is bound again. A failure-mode id the taxonomy does not list is a warning, never a gate clause. Judge calibration is display only: below 80% precision or recall, or older than 30 days, is flagged and nothing is blocked.

## Version comparison

Improve compares each run with the latest earlier run of the same suite on a different `harness_version`, task by task, and only where the task hash is unchanged. Change one thing between versions, rerun with the same trials, and the delta is attributable.
