namespace CoachingIA.Harness.Tests;

/// <summary>
/// Échafaudage. À remplir par la tâche qui émet, au hook Stop, un vrai span
/// enfant OI.Kind.Llm sous le span de tour (spec §4.2) : présence de
/// llm.model_name et des compteurs llm.token_count.* sur ce span.
/// </summary>
public static class PhoenixLlmTests
{
    public static void Run(Action<bool, string> check)
    {
    }
}
