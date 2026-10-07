
## Where commands run

Verify checks and run_command run in the factory runner container (docker/runner/Dockerfile).
Plan, write and run only what it can run:
- Tools: .NET 9 SDK (with the .NET 8 reference packs `p compile` needs), P 3.1.0 as `p`, JDK 17,
  bash and coreutils. Nothing else is installed, and nothing can be installed.
- There is no network. `dotnet restore` runs once before the checks with the network on, so
  NuGet packages referenced by the repository's projects are available; nothing else is.
- There is no Docker daemon: Testcontainers, docker compose and anything else that starts a
  container cannot run. Use an in-process substitute (an EF Core SQLite in-memory database, an
  in-memory fake) and, if the real engine matters, add a `manual` verify entry for that part.
- There is no `pobserve` command. To check logs against a P spec run
  `pobserve-check <specs-dir> <SpecName> <log file|dir>` (exit 0 when the spec holds; logs with no
  spec events fail). To prove the spec rejects a log, add `--expect-violation` (exit 0 only on a
  violation); never negate it with `!`, which also passes when the spec does not compile.
- Each verify check starts from the tree as it was before the checks: logs and generated files
  from one check are gone before the next. A check that reads logs must produce them itself, in
  the same command.
- Keep a P model in its own directory, never inside a .NET project directory: `p compile`
  generates C# there, and the project around it would compile it.
