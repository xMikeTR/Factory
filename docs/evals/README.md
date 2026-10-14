# Factory evals — S2's reference copies

Slice 4 (decided 2026-10-11, imported 2026-10-14): the factory evals leave designc. designc keeps no harness and, after its own removal slice, no `suite_run`/`grader`/`suite_run_finished`/`factory_task_bound`/`failure_mode_classified` kinds.

- `factory-evals-contract.md` — wire contract designc used to read (copied from Designc `docs/factory-evals-contract.md`, unchanged). Reference for the shape of a suite run; S2 does not write these kinds to the registry.
- `Evaluation-Bible.md` — the method ("Where evals live", "Eval harness design"). Copied unchanged.

The eval suite itself lives on the user's work VM; integration with S2 happens there. Nothing in this repo runs it.
