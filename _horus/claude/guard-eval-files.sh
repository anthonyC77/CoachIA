#!/bin/sh
# Garde-fou global — PreToolUse sur Edit|Write|MultiEdit|Bash (settings.json).
# Protège les fichiers d'étalon : golden sets, baseline, schémas générés, politique.
# permissions.deny couvre Edit/Write ; ce hook ferme la porte Bash (sed -i, tee, >, cp, mv, python -c …).
# Principe : une action dangereuse empêchée uniquement par le prompt n'est pas empêchée.
# Sortie 0 = autorisé, 2 = bloqué.

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty')
PROTECTED='eval/golden_set\.jsonl|eval/tasks_golden\.jsonl|eval/baseline\.json|eval/schemas/|eval/policy/'

case "$TOOL" in
  Edit|Write|MultiEdit|NotebookEdit)
    FILE=$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // .tool_input.path // empty')
    case "$FILE" in "$CLAUDE_PROJECT_DIR"/*) FILE=${FILE#"$CLAUDE_PROJECT_DIR"/} ;; esac
    if printf '%s' "$FILE" | grep -Eq "^($PROTECTED)"; then
      echo "BLOQUÉ : $FILE est un fichier d'étalon. On modifie le type et on régénère (schémas), ou l'humain décide (golden, baseline)." >&2
      exit 2
    fi ;;
  Bash)
    CMD=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // empty')
    # Un nouveau point de référence est une décision humaine : jamais depuis un agent.
    if printf '%s' "$CMD" | grep -q -- '--write-baseline'; then
      echo "BLOQUÉ : --write-baseline se lance depuis un terminal humain, pas depuis un agent." >&2
      exit 2
    fi
    # Autorisé : les scripts d'eval eux-mêmes (gen_tool_schemas régénère les schémas depuis les types).
    if printf '%s' "$CMD" | grep -Eq '^[[:space:]]*python3 eval/(run_eval|run_tasks_eval|gen_tool_schemas|check_tool_schemas|check_nodes|width_report)\.py'; then
      exit 0
    fi
    if printf '%s' "$CMD" | grep -Eq "($PROTECTED)" && \
       printf '%s' "$CMD" | grep -Eq '(sed -i|tee |>|>>|cp |mv |rm |truncate|python3? -c|perl -|dd )'; then
      echo "BLOQUÉ : commande qui écrit dans un fichier d'étalon : $CMD" >&2
      exit 2
    fi ;;
esac
exit 0
