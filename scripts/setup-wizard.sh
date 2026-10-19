#!/usr/bin/env bash
# factory-s2 setup wizard: implementer model, independent reviewer, sandbox, telemetry.
#
#   scripts/setup-wizard.sh
#
# factory-s2 is configured only by FACTORY_* environment variables, so this writes one
# env file, ~/.config/factory-s2/env (mode 600), outside every repo. Load it with:
#   source ~/.config/factory-s2/env
# Nothing is sent anywhere unless you answer y to a connection test, and the only calls
# are free auth checks (model list, Langfuse projects). No model is ever prompted.
set -u

ENVDIR="${XDG_CONFIG_HOME:-$HOME/.config}/factory-s2"
ENVFILE="$ENVDIR/env"
OUT=()                    # lines for the env file
add() { OUT+=("export $1=$(printf %q "$2")"); }

ask() { local d="${2:-}"; read -r -p "$1${d:+ [$d]}: " REPLY; REPLY="${REPLY:-$d}"; }
yes() { local a; read -r -p "$1 [y/N]: " a; [[ "$a" =~ ^[Yy] ]]; }
secret() { # secret VAR  (hidden entry, Enter to skip)
  local v; read -r -s -p "  Paste the key for $1 (hidden, Enter to skip): " v; echo
  [ -n "$v" ] && { add "$1" "$v"; export "$1=$v"; }
}

# provider SIDE  where SIDE is "" (implementer) or "REVIEWER_" -> sets FACTORY_[REVIEWER_]...
provider() {
  local p="$1" label="$2" prov url key
  echo "  1) Anthropic API (needs an Anthropic key)"
  echo "  2) OpenAI-compatible gateway (LiteLLM/APIM, per-user key)"
  echo "  3) Claude on Vertex AI (gcloud Application Default Credentials, no key)"
  ask "$label provider" 3; prov="$REPLY"
  if [ "$prov" = 3 ]; then
    add "FACTORY_${p}PROVIDER" vertex
    ask "  Google Cloud project" "${GOOGLE_CLOUD_PROJECT:-}"; add "FACTORY_${p}VERTEX_PROJECT" "$REPLY"
    ask "  Region" "${CLOUD_ML_REGION:-global}"; add "FACTORY_${p}VERTEX_REGION" "$REPLY"
    if command -v gcloud >/dev/null; then
      gcloud auth application-default print-access-token >/dev/null 2>&1 && echo "    Application Default Credentials found." \
        || echo "    No credentials yet. Run: gcloud auth application-default login"
    else echo "    gcloud not found. Install it, then: gcloud auth application-default login"; fi
    local f="claude-opus-5-5" st="claude-sonnet-5-5" sm="claude-haiku-4-5" fam=anthropic
    if [ -n "$p" ]; then f="google/gemini-2.5-pro"; st="google/gemini-2.5-pro"; sm="google/gemini-2.5-flash"; fam=google
      echo "  Any Vertex model works: Claude ids use the Anthropic client, everything else (Gemini, Model Garden) the OpenAI-compatible endpoint."; fi
    ask "  Frontier model" "$f"; add "FACTORY_${p}MODEL_FRONTIER" "$REPLY"
    ask "  Standard model" "$st"; add "FACTORY_${p}MODEL_STANDARD" "$REPLY"
    ask "  Small model"    "$sm"; add "FACTORY_${p}MODEL_SMALL" "$REPLY"
    ask "  Model family (anthropic, google, meta, ...)" "$fam"; FAMILY="$REPLY"
  elif [ "$prov" = 1 ]; then
    add "FACTORY_${p}PROVIDER" anthropic
    ask "  Base URL (blank = api.anthropic.com)" ""; url="$REPLY"
    [ -n "$url" ] && add "FACTORY_${p}GATEWAY_URL" "$url"
    key=ANTHROPIC_API_KEY
    if [ -z "${p}" ]; then secret "$key"; else secret "FACTORY_REVIEWER_GATEWAY_KEY"; fi
    if [ -z "$p" ] && [ -n "${!key:-}" ] && yes "  Test the key (lists models, free)?"; then
      curl -fsS -m 15 -H "x-api-key: ${!key}" -H "anthropic-version: 2023-06-01" "${url:-https://api.anthropic.com}/v1/models" >/dev/null \
        && echo "    OK" || echo "    Failed. Check the key."; fi
    local f="claude-opus-5-5" st="claude-sonnet-5-5" sm="claude-haiku-4-5" fam=anthropic
    if [ -n "$p" ]; then f="google/gemini-2.5-pro"; st="google/gemini-2.5-pro"; sm="google/gemini-2.5-flash"; fam=google
      echo "  Any Vertex model works: Claude ids use the Anthropic client, everything else (Gemini, Model Garden) the OpenAI-compatible endpoint."; fi
    ask "  Frontier model" "$f"; add "FACTORY_${p}MODEL_FRONTIER" "$REPLY"
    ask "  Standard model" "$st"; add "FACTORY_${p}MODEL_STANDARD" "$REPLY"
    ask "  Small model"    "$sm"; add "FACTORY_${p}MODEL_SMALL" "$REPLY"
    ask "  Model family (anthropic, google, meta, ...)" "$fam"; FAMILY="$REPLY"
  else
    add "FACTORY_${p}PROVIDER" openai
    ask "  Gateway URL" "https://gateway/litellm/v1"; url="$REPLY"; add "FACTORY_${p}GATEWAY_URL" "$url"
    secret "FACTORY_${p}GATEWAY_KEY"
    local k="FACTORY_${p}GATEWAY_KEY"
    if [ -z "$p" ] && [ -n "${!k:-}" ] && yes "  Test the key (lists models, free)?"; then
      curl -fsS -m 15 -H "Authorization: Bearer ${!k}" "${url%/}/models" >/dev/null && echo "    OK" || echo "    Failed. Check the URL and key."; fi
    ask "  Frontier model alias" ""; add "FACTORY_${p}MODEL_FRONTIER" "$REPLY"
    ask "  Standard model alias" ""; add "FACTORY_${p}MODEL_STANDARD" "$REPLY"
    ask "  Small model alias"    ""; add "FACTORY_${p}MODEL_SMALL" "$REPLY"
    ask "  Model family behind it (anthropic, openai, google, ...)" "openai"; FAMILY="$REPLY"
  fi
  add "FACTORY_${p}MODEL_FAMILY" "$FAMILY"
}

echo; echo "== factory-s2 setup =="
echo "Step 1 of 4: implementer model (writes the code)"
provider "" "Implementer"
IMPL_FAMILY="$FAMILY"

echo; echo "Step 2 of 4: reviewer (must be a different model family)"
if yes "Do you have a second provider or family for the reviewer?"; then
  provider "REVIEWER_" "Reviewer"
  [ "$FAMILY" = "$IMPL_FAMILY" ] && echo "  Warning: both are '$FAMILY'. Start-up will refuse unless you allow same-family below."
else
  echo "  Without one, only dry-run merges are allowed (not --github), and reviews are stamped same-family."
  yes "  Allow same-family review (FACTORY_REVIEWER_SAME_FAMILY=allow)?" && add FACTORY_REVIEWER_SAME_FAMILY allow
fi

echo; echo "Step 3 of 4: sandbox (where generated code runs)"
echo "  1) docker (default, isolated)   2) docker, falling back to local   3) local only (no isolation)"
ask "Choose" 1
case "$REPLY" in 2) add FACTORY_SANDBOX docker,local ;; 3) add FACTORY_SANDBOX local ;; *) add FACTORY_SANDBOX docker ;; esac
if grep -q 'FACTORY_SANDBOX=.*docker' < <(printf '%s\n' "${OUT[@]}") && ! command -v docker >/dev/null; then
  echo "  Note: docker is not installed or not on PATH."; fi

echo; echo "Step 4 of 4: telemetry (optional)"
if yes "Send traces to Langfuse?"; then
  ask "  Langfuse URL" "https://cloud.langfuse.com"; H="$REPLY"; add LANGFUSE_HOST "$H"
  read -r -p "  Public key (pk-lf-..., not secret): " PK; [ -n "$PK" ] && { add LANGFUSE_PUBLIC_KEY "$PK"; export LANGFUSE_PUBLIC_KEY="$PK"; }
  secret LANGFUSE_SECRET_KEY
  if [ -n "${LANGFUSE_PUBLIC_KEY:-}" ] && [ -n "${LANGFUSE_SECRET_KEY:-}" ] && yes "  Test the keys (reads your project name only)?"; then
    curl -fsS -m 15 -u "$LANGFUSE_PUBLIC_KEY:$LANGFUSE_SECRET_KEY" "${H%/}/api/public/projects" >/dev/null && echo "    OK" || echo "    Failed. Check the URL and keys."; fi
  yes "  Also put prompts and replies on spans (FACTORY_OTEL_CAPTURE_CONTENT)?" && add FACTORY_OTEL_CAPTURE_CONTENT 1
elif yes "Use another OTLP endpoint instead?"; then
  ask "  OTEL_EXPORTER_OTLP_ENDPOINT" ""; [ -n "$REPLY" ] && add OTEL_EXPORTER_OTLP_ENDPOINT "$REPLY"
  echo "  Add OTEL_EXPORTER_OTLP_HEADERS to the env file by hand if it needs a token."
fi

echo; echo "Review: $ENVFILE would contain these names (values hidden):"
printf '%s\n' "${OUT[@]}" | sed -E 's/^export ([A-Z_]+)=.*/  \1/'
if yes "Write it?"; then
  mkdir -p "$ENVDIR"; chmod 700 "$ENVDIR"
  [ -f "$ENVFILE" ] && cp "$ENVFILE" "$ENVFILE.bak"
  printf '# written by scripts/setup-wizard.sh\n' > "$ENVFILE"; printf '%s\n' "${OUT[@]}" >> "$ENVFILE"; chmod 600 "$ENVFILE"
  echo "Written. Load it, then check without a model:"
  echo "  source $ENVFILE"
  echo "  dotnet run --project src/Factory.S2 -- validate --help"
else echo "Nothing written."; fi
