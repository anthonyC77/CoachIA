namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Ce qu'on attend d'un signal, et comment le dire. C'est le seul endroit où
/// vivent les cibles et le sens de lecture — un signal dont on ignore la
/// direction ne peut ni déclencher une observation, ni compter comme un progrès.
/// </summary>
public sealed record SignalSpec(
    string Key, int Level, bool HigherIsBetter, double Target,
    string Complaint, string Praise, string Advice, bool IsRatio = true)
{
    /// <summary>
    /// L'écart à la cible, normalisé, positif quand le signal est en défaut.
    /// Sert à classer les observations : on parle d'abord de ce qui manque le plus.
    /// </summary>
    public double Gap(double value)
    {
        if (double.IsNaN(value)) return double.NaN;
        var scale = IsRatio ? 1.0 : Math.Max(1.0, Target);
        var gap = HigherIsBetter ? Target - value : value - Target;
        return gap / scale;
    }

    public bool Meets(double value) => !double.IsNaN(value) && Gap(value) <= 0;
}

/// <summary>
/// La grille des signaux notés, servie depuis le corpus de maturité.
///
/// C'est une façade statique et non une dépendance injectée, délibérément :
/// huit fichiers la consultent comme une constante, et la transformer en
/// service aurait fait traverser un paramètre à toute la chaîne de rendu pour
/// une valeur qui ne change jamais en cours d'exécution. Le corpus est monté
/// une fois, au démarrage, par <see cref="Use"/> ; avant cet appel, la version
/// intégrée répond déjà — un outil lancé sans dossier de lentilles fonctionne.
/// </summary>
public static class SignalSpecs
{
    private static MaturityCorpus _corpus = MaturityCorpus.BuiltIn;

    /// <summary>Le corpus en vigueur : problématiques, moments, signaux non notés compris.</summary>
    public static MaturityCorpus Corpus => _corpus;

    /// <summary>
    /// Monte un corpus chargé depuis les données. À appeler une fois, avant tout
    /// calcul — un changement en cours de bilan rendrait la sortie non
    /// reproductible, donc inarchivable.
    /// </summary>
    public static void Use(MaturityCorpus corpus) => _corpus = corpus;

    /// <summary>Les signaux qui portent une cible. Un signal sans cible ne reproche rien.</summary>
    public static IReadOnlyList<SignalSpec> All => _corpus.Specs;

    public static SignalSpec? Find(string key) => _corpus.Spec(key);
}
