#!/bin/sh
# Garde-fou de périmètre — PreToolUse (Read/Edit/Write) dans le frontmatter d'un agent.
# Usage : guard-paths.sh tests-only  -> testeur : écrit dans tests/, lit specs/, tests/, contrats publics
#         guard-paths.sh no-tests    -> codeur  : tout sauf tests/, specs/, eval/
#         guard-paths.sh eval-only   -> juge    : lit eval/runs, eval/tasks*, eval/schemas ; écrit eval/runs/judge/ seulement.
#                                       Jamais src/ : l'eval ne dispose d'aucun accès aux internes.
# Sortie 0 = autorisé, 2 = bloqué (stderr renvoyé à l'agent).
# Limite connue : ne couvre pas les écritures via Bash (sed -i, tee) — voir guard-eval-files.sh pour eval/.

MODE="$1"
INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty')
FILE=$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // .tool_input.path // empty')
[ -z "$FILE" ] && exit 0

case "$FILE" in
  "$CLAUDE_PROJECT_DIR"/*) FILE=${FILE#"$CLAUDE_PROJECT_DIR"/} ;;
esac

is_test=0;     case "$FILE" in tests/*|front/src/*.spec.ts) is_test=1 ;; esac
is_spec=0;     case "$FILE" in specs/*|eval/*|docs/*) is_spec=1 ;; esac
is_contract=0; case "$FILE" in src/*/Contracts/*|src/*/I[A-Z]*.cs) is_contract=1 ;; esac
is_eval_pub=0; case "$FILE" in eval/runs/*|eval/tasks/*|eval/tasks_golden.jsonl|eval/schemas/*|eval/policy/*) is_eval_pub=1 ;; esac
is_judge_out=0; case "$FILE" in eval/runs/judge/*) is_judge_out=1 ;; esac
is_write=0;    case "$TOOL" in Edit|Write|MultiEdit|NotebookEdit) is_write=1 ;; esac

case "$MODE" in
  tests-only)
    if [ "$is_write" = 1 ] && [ "$is_test" = 0 ]; then
      echo "BLOQUÉ (testeur) : écriture hors tests/ interdite : $FILE" >&2; exit 2
    fi
    if [ "$is_write" = 0 ] && [ "$is_test" = 0 ] && [ "$is_spec" = 0 ] && [ "$is_contract" = 0 ]; then
      echo "BLOQUÉ (testeur) : lecture de l'implémentation interdite : $FILE. Utilise la spec et les contrats publics (src/*/Contracts, I*.cs)." >&2; exit 2
    fi ;;
  no-tests)
    if [ "$is_write" = 1 ] && { [ "$is_test" = 1 ] || [ "$is_spec" = 1 ]; }; then
      echo "BLOQUÉ (codeur) : $FILE est hors périmètre. Si un test te semble faux, termine avec 'TEST_CONTESTÉ: <nom> — <raison>'." >&2; exit 2
    fi ;;
  eval-only)
    if [ "$is_write" = 1 ] && [ "$is_judge_out" = 0 ]; then
      echo "BLOQUÉ (juge) : le juge n'écrit que dans eval/runs/judge/ : $FILE" >&2; exit 2
    fi
    if [ "$is_write" = 0 ] && [ "$is_eval_pub" = 0 ]; then
      echo "BLOQUÉ (juge) : lecture hors du contrat public interdite : $FILE. Le juge ne voit que les runs, les tâches et les schémas." >&2; exit 2
    fi ;;
esac
exit 0
