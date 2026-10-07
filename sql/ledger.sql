-- Shared ledger: append-only, written by Systems 1 and 2, read by System 4.
-- Same columns as Factory.Harness.LedgerEvent (JSONL uses snake_case keys with these names).
-- Postgres; for Azure SQL use NVARCHAR(MAX) + ISJSON for payload and DATETIMEOFFSET for ts.

CREATE TABLE IF NOT EXISTS ledger (
    id           BIGSERIAL PRIMARY KEY,
    ts           TIMESTAMPTZ   NOT NULL DEFAULT now(),
    run_id       TEXT          NOT NULL,
    trace_id     TEXT,                      -- joins to the Langfuse trace
    system       TEXT          NOT NULL,    -- S1 | S2 | S4
    stage        TEXT          NOT NULL,
    kind         TEXT          NOT NULL,    -- see LedgerKinds
    task_id      TEXT,
    team_id      TEXT,
    requirement  TEXT,
    element      TEXT,
    decision     TEXT,
    code         TEXT,                      -- lint / finding code, e.g. DC002
    severity     TEXT,
    outcome      TEXT,
    model        TEXT,
    tokens_in    BIGINT,
    tokens_out   BIGINT,
    cost         NUMERIC(12, 6),
    payload      JSONB
);

-- Append-only: no updates or deletes for the application role.
REVOKE UPDATE, DELETE ON ledger FROM PUBLIC;

CREATE INDEX IF NOT EXISTS ledger_run_task  ON ledger (run_id, task_id, id);   -- task replay on resume
CREATE INDEX IF NOT EXISTS ledger_kind_ts   ON ledger (kind, ts);              -- System 4 weekly queries
CREATE INDEX IF NOT EXISTS ledger_element   ON ledger (element) WHERE element IS NOT NULL;

-- ---------- System 4 starter queries (Phase A weekly review) ----------

-- Tokens per passing task, by model tier (turn rows carry the tier in `model`).
CREATE OR REPLACE VIEW s2_tokens_per_passing_task AS
WITH passed AS (
    SELECT DISTINCT run_id, task_id FROM ledger
    WHERE kind = 'task' AND outcome = 'Passed'
)
SELECT t.model,
       count(DISTINCT (t.run_id, t.task_id))                       AS passing_tasks,
       sum(coalesce(t.tokens_in, 0) + coalesce(t.tokens_out, 0))    AS tokens,
       sum(coalesce(t.tokens_in, 0) + coalesce(t.tokens_out, 0))
         / nullif(count(DISTINCT (t.run_id, t.task_id)), 0)         AS tokens_per_passing_task
FROM ledger t JOIN passed p USING (run_id, task_id)
WHERE t.kind = 'turn'
GROUP BY t.model;

-- Turns and verify attempts per task (reruns are the main cost lever).
CREATE OR REPLACE VIEW s2_task_effort AS
SELECT run_id, task_id,
       count(*) FILTER (WHERE payload->>'type' = 'turn_started')     AS turns,
       count(*) FILTER (WHERE payload->>'type' = 'verify_requested') AS verify_attempts,
       max(outcome) FILTER (WHERE outcome IN ('Passed', 'Failed'))   AS final_state
FROM ledger WHERE kind = 'task'
GROUP BY run_id, task_id;

-- Needs-decision escalations: each one is a question System 1 should have asked.
CREATE OR REPLACE VIEW s2_needs_decision AS
SELECT ts, run_id, task_id, payload->>'Question' AS question, payload->>'Source' AS source
FROM ledger WHERE kind = 'needs_decision' AND outcome = 'raised';

-- Scope violations and policy denials: signals for prompt or policy proposals.
CREATE OR REPLACE VIEW s2_guardrail_hits AS
SELECT ts, run_id, task_id, kind, outcome, payload
FROM ledger
WHERE kind = 'scope_violation' OR (kind = 'tool_call' AND outcome = 'denied');
