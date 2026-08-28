using System.Text.RegularExpressions;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>Un critère de la grille, et ce qu'on cherche pour le cocher.</summary>
public sealed record RubricItem(
    string Key, string Label, string Question, string Fix, Regex Cue, bool Essential = true);

/// <summary>
/// La grille de lecture d'un prompt. Sept critères, dont cinq essentiels.
///
/// Elle ne mesure pas la qualité d'écriture : elle cherche des informations que
/// l'agent ne peut pas deviner. Un prompt qui les porte évite la reprise&nbsp;;
/// un prompt qui les omet la provoque — et c'est cette reprise, mesurée dans les
/// traces, qui prouve l'utilité de la grille plutôt qu'un avis de style.
/// </summary>
public static class PromptRubric
{
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly IReadOnlyList<RubricItem> Items =
    [
        new("objectif", "Objectif",
            "Le verbe et l'objet sont-ils explicites ?",
            "Commencez par un verbe d'action et l'objet précis : « ajoute », « corrige », « extrais » — pas « regarde » ni « occupe-toi de ».",
            R(@"\b(ajoute|crée|créer|corrige|corriger|implémente|implémenter|écris|écrire|extrais|extraire|remplace|remplacer|supprime|refactor|migre|migrer|documente|documenter|teste|tester|optimise|optimiser|renomme|déplace)\b")),

        new("perimetre", "Périmètre",
            "Sait-on où intervenir, et où ne pas intervenir ?",
            "Nommez le fichier, le module ou la zone concernée — et, si le risque existe, ce qu'il ne faut pas toucher.",
            R(@"(\b[\w\-/\.]+\.(cs|ts|js|py|json|html|scss|css|sql|md|yml|yaml|razor|xaml)\b)|\b(dans le (module|dossier|service|projet|fichier)|uniquement dans|sans toucher|ne touche pas|hors de)\b")),

        new("acceptation", "Critère d'acceptation",
            "Comment saura-t-on que c'est fini ?",
            "Dites à quoi ressemble le résultat attendu : un test qui passe, une sortie précise, un comportement observable.",
            R(@"\b(doit|devra|il faut que|jusqu'à ce que|critère|attendu|test[s]? (vert|passe|passent)|quand .* (marche|fonctionne)|vérifie que|valide que|renvoie|retourne|affiche|sans (casser|régression))\b")),

        new("contraintes", "Contraintes",
            "Y a-t-il des limites à respecter ?",
            "Précisez ce qui ne doit pas bouger : compatibilité, style, dépendances, performance, API publique.",
            R(@"\b(sans (casser|changer|ajouter|modifier)|garde|garder|conserve|conserver|compatib|ne pas (utiliser|ajouter)|reste en|en gardant|même (style|convention)|pas de nouvelle dépendance|performance)\b"), Essential: false),

        new("sortie", "Format de sortie",
            "Sait-on ce qu'il faut rendre ?",
            "Dites ce que vous attendez en retour : le code modifié, un patch, une explication, une commande à lancer.",
            R(@"\b(renvoie|retourne|donne[- ]moi|écris dans|dans un fichier|au format|en (json|markdown|tableau|liste)|un patch|explique|résume)\b"), Essential: false),

        new("contexte", "Contexte utile",
            "L'agent a-t-il ce qu'il ne peut pas deviner ?",
            "Ajoutez la décision passée, la contrainte métier ou l'historique qui explique pourquoi c'est ainsi.",
            R(@"\b(parce que|car|le but est|on avait|historiquement|la contrainte est|attention[,:]|à savoir|contexte)\b")),

        new("autonomie", "Autonomie",
            "Sait-on jusqu'où aller seul ?",
            "Indiquez la marge : « va jusqu'au bout », « propose avant d'appliquer », « demande si tu hésites ».",
            R(@"\b(va jusqu'au bout|jusqu'à ce que .* passe|propose|demande[- ]moi|ne (commit|pousse)|avant d'appliquer|sans me demander|autonom)\b")),
    ];

    /// <summary>Les critères manquants, essentiels d'abord.</summary>
    public static List<RubricItem> Missing(string prompt)
        => Items.Where(i => !i.Cue.IsMatch(prompt))
                .OrderByDescending(i => i.Essential)
                .ToList();

    public static List<RubricItem> Present(string prompt)
        => Items.Where(i => i.Cue.IsMatch(prompt)).ToList();

    /// <summary>Part des critères essentiels couverts. Le score du palier 1, en somme.</summary>
    public static double Coverage(string prompt)
    {
        var essentials = Items.Where(i => i.Essential).ToList();
        if (essentials.Count == 0) return 1;
        return (double)essentials.Count(i => i.Cue.IsMatch(prompt)) / essentials.Count;
    }
}
