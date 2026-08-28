#!/usr/bin/env bash
# Equivalent bash de smoke-test.ps1, pour WSL ou un poste Linux.
set -euo pipefail
BASE="${1:-http://localhost:8080}"
S="smoke-$(date +%H%M%S)"; P="$S-p1"

hook() { curl -sf -m 5 -X POST "$BASE/hooks/$1" -H 'Content-Type: application/json' -d "$2" >/dev/null \
  && echo "  ok   $1" || { echo "  FAIL $1"; exit 1; }; }

curl -sf -m 5 "$BASE/health" >/dev/null || { echo "Le harnais ne repond pas sur $BASE"; exit 1; }
echo "Session synthetique $S"
hook session-start   "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"$S\",\"session_start_reason\":\"startup\"}"
hook user-prompt     "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"user_prompt\":\"Ajoute un test de non-regression.\"}"
hook post-tool       "{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"tool_name\":\"Grep\",\"tool_input\":{\"pattern\":\"ParseHook\"},\"tool_response\":\"4 matches\",\"tool_execution_time_ms\":180}"
hook post-tool-fail  "{\"hook_event_name\":\"PostToolUseFailure\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dotnet test\"},\"tool_execution_time_ms\":5200,\"error_message\":\"1 test failed\"}"
hook pre-compact     "{\"hook_event_name\":\"PreCompact\",\"session_id\":\"$S\",\"prompt_id\":\"$P\"}"
hook subagent-start  "{\"hook_event_name\":\"SubagentStart\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"agent_id\":\"agent-1\",\"agent_type\":\"Explore\"}"
hook subagent-stop   "{\"hook_event_name\":\"SubagentStop\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"agent_id\":\"agent-1\"}"
hook post-tool       "{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"Parser.cs\"},\"tool_response\":\"ok\",\"tool_execution_time_ms\":90}"
hook stop            "{\"hook_event_name\":\"Stop\",\"session_id\":\"$S\",\"prompt_id\":\"$P\",\"stop_reason\":\"end_turn\",\"last_assistant_message\":\"Test ajoute et vert.\"}"
hook session-end     "{\"hook_event_name\":\"SessionEnd\",\"session_id\":\"$S\"}"
echo; curl -s -m 5 "$BASE/status"; echo
echo "Ouvrez http://localhost:6006, projet 'coachingia' : une trace de 7 spans doit apparaitre."
