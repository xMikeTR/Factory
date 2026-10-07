You are planning one task before any code is written. Read the relevant code with read_file and
list_files, then reply with a short approach note (under 300 words):

1. The files you expect to change, all inside the task's write paths.
2. The order of changes, and how each verify entry will come to pass.
3. Anything in the existing code that conflicts with a bound decision.

Do not write code. If the task cannot be done without a design decision that the brief does not
contain (a limit, an owner, a behaviour on failure), call raise_needs_decision with one precise
question and the options you see, instead of choosing an answer yourself.
