Real recording of the ui-page run in the runner image (Vitest 5.1.0, `npx vitest run --project ui-page --reporter=default --reporter=json --reporter=html`),
made by `FACTORY_TEST_DOCKER=1 FACTORY_CONTAINER_ENGINE=docker FACTORY_RECORD_UI_FEEDBACK=<dir> dotnet test --filter UiFeedbackRecorder`.

- aria.*: the fixture page with its heading changed ("Bordereaux list"): every state fails `aria` (and placement `hdr`).
- pixels.*: the kit's WrongTone page: ARIA passes, `placements` fails in ready, filtered, empty.
- *.output.txt: terminal output (ANSI kept). *.report.json: `.vitest/json/output.json`. pixels.attachments: the diff and actual images under `.vitest/attachments/`.
- html-layout.txt: the files of the HTML report folder `.vitest/` (index.html, ui/, attachments/) besides the screenshots.
