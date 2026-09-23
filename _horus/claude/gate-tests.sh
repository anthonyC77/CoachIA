#!/bin/sh
# Porte de sortie du codeur — SubagentStop (settings.json, matcher codeur).
# Refuse la fin de tâche tant que les tests sont rouges. 1 exécution par passage, pas 20.
# Sortie 0 = peut s'arrêter, 2 = continue (stderr renvoyé à l'agent).
# [hypothèse à tester — LIVRABLE.md, test n°1] : le code 2 sur SubagentStop bloque l'arrêt.
# Si le test échoue : déplacer ce hook dans le frontmatter de codeur.md sous `hooks.Stop`.

INPUT=$(cat)
ACTIVE=$(printf '%s' "$INPUT" | jq -r '.stop_hook_active // false')
[ "$ACTIVE" = "true" ] && exit 0            # anti-boucle : on ne re-bloque pas un second arrêt
AGENT=$(printf '%s' "$INPUT" | jq -r '.agent_type // .agent_name // empty')
[ -n "$AGENT" ] && [ "$AGENT" != "codeur" ] && exit 0

cd "$CLAUDE_PROJECT_DIR" || exit 0
CHANGED=$(git diff --name-only HEAD 2>/dev/null; git ls-files --others --exclude-standard 2>/dev/null)
[ -z "$CHANGED" ] && exit 0

LOG=$(mktemp)
if printf '%s\n' "$CHANGED" | grep -q '\.cs$'; then
  if ! dotnet test Horus.sln --no-restore --nologo -v q >"$LOG" 2>&1; then
    echo "TESTS ROUGES (back). Extrait :" >&2
    grep -E 'Failed|error CS|Échec' "$LOG" | head -20 >&2
    rm -f "$LOG"; exit 2
  fi
fi
if printf '%s\n' "$CHANGED" | grep -q '^front/.*\.ts$'; then
  if ! (cd front && npx ng test --watch=false --browsers=ChromeHeadless >"$LOG" 2>&1); then
    echo "TESTS ROUGES (front). Extrait :" >&2
    grep -E 'FAILED|Error' "$LOG" | head -20 >&2
    rm -f "$LOG"; exit 2
  fi
fi
rm -f "$LOG"
exit 0
