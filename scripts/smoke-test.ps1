# Rejoue une session synthetique contre le harnais, sans toucher a Claude Code.
# Sert a valider la phase 00 : le tuyau hooks -> harnais -> Phoenix.
#
#   pwsh scripts/smoke-test.ps1
#
param([string]$BaseUrl = "http://localhost:8080")

function Send-Hook([string]$Route, [hashtable]$Body) {
    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    try {
        Invoke-RestMethod -Uri "$BaseUrl/hooks/$Route" -Method Post -ContentType 'application/json' -Body $json -TimeoutSec 5 | Out-Null
        Write-Host "  ok   $Route"
    } catch {
        Write-Host "  FAIL $Route : $_" -ForegroundColor Red
        exit 1
    }
}

$session = "smoke-" + (Get-Date -Format "HHmmss")
$prompt  = "$session-p1"

Write-Host "Harnais : $BaseUrl"
try { Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 5 | Out-Null }
catch { Write-Host "Le harnais ne repond pas. Lancez-le d'abord : dotnet run --project src/CoachingIA.Harness" -ForegroundColor Red; exit 1 }

Write-Host "`nSession synthetique $session"
Send-Hook "session-start" @{ hook_event_name="SessionStart"; session_id=$session; cwd=(Get-Location).Path; permission_mode="auto"; session_start_reason="startup" }
Send-Hook "user-prompt"   @{ hook_event_name="UserPromptSubmit"; session_id=$session; prompt_id=$prompt; user_prompt="Ajoute un test de non-regression sur le parseur de hooks." }
Send-Hook "post-tool"     @{ hook_event_name="PostToolUse"; session_id=$session; prompt_id=$prompt; tool_name="Grep"; tool_use_id="toolu_1"; tool_input=@{ pattern="ParseHook" }; tool_response="4 matches"; tool_execution_time_ms=180 }
Send-Hook "post-tool-fail" @{ hook_event_name="PostToolUseFailure"; session_id=$session; prompt_id=$prompt; tool_name="Bash"; tool_input=@{ command="dotnet test" }; tool_execution_time_ms=5200; error_message="1 test failed" }
Send-Hook "pre-compact"   @{ hook_event_name="PreCompact"; session_id=$session; prompt_id=$prompt }
Send-Hook "subagent-start" @{ hook_event_name="SubagentStart"; session_id=$session; prompt_id=$prompt; agent_id="agent-1"; agent_type="Explore" }
Send-Hook "subagent-stop"  @{ hook_event_name="SubagentStop"; session_id=$session; prompt_id=$prompt; agent_id="agent-1"; last_assistant_message="12 fichiers concernes" }
Send-Hook "post-tool"     @{ hook_event_name="PostToolUse"; session_id=$session; prompt_id=$prompt; tool_name="Edit"; tool_input=@{ file_path="Parser.cs" }; tool_response="ok"; tool_execution_time_ms=90 }
Send-Hook "stop"          @{ hook_event_name="Stop"; session_id=$session; prompt_id=$prompt; stop_reason="end_turn"; last_assistant_message="Test ajoute et vert." }
Send-Hook "session-end"   @{ hook_event_name="SessionEnd"; session_id=$session }

$status = Invoke-RestMethod -Uri "$BaseUrl/status" -TimeoutSec 5
Write-Host "`nSpans encore ouverts : $($status.openSpans) (0 attendu)"

Write-Host @"

Ouvrez http://localhost:6006 et cherchez le projet 'coachingia'.
Vous devez y voir une trace de 7 spans :

  session (AGENT)
   └─ turn (CHAIN)
       ├─ tool.Grep (TOOL, ~180 ms)
       ├─ tool.Bash (TOOL, ~5,2 s, en erreur)
       ├─ context.compact (CHAIN)
       ├─ agent.Explore (AGENT)
       └─ tool.Edit (TOOL, ~90 ms)

Si la trace est la, la phase 00 est validee.
"@
