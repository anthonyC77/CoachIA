using System.Text;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Met le bilan en forme. Markdown, et rien d'autre pour l'instant : il s'ouvre
/// dans l'éditeur où le travail se fait, il se relit six mois plus tard, il se
/// versionne. Une page web viendra si le besoin s'en fait sentir — pas avant
/// qu'on sache si le rituel tient.
///
/// L'ordre est fixe et il n'est pas anodin : ce qui a progressé d'abord, les
/// observations ensuite, le défi à la fin. Un bilan qui ouvre sur les reproches
/// se lit une fois.
/// </summary>
public static class ReviewRenderer
{
    // Le projet tourne en globalisation invariante : pas d'ICU, donc pas de
    // culture « fr-FR ». Les quelques mots de calendrier dont un bilan a besoin
    // sont écrits ici plutôt que de traîner une dépendance pour douze noms.
    private static readonly string[] Months =
    [
        "janvier", "février", "mars", "avril", "mai", "juin",
        "juillet", "août", "septembre", "octobre", "novembre", "décembre",
    ];
    private static readonly string[] Days =
    [
        "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi",
    ];

    public static string ToMarkdown(WeeklyReview review, LensWriter writer)
    {
        var b = new StringBuilder();
        var lens = writer.Lens.Id == "neutre" ? "" : $" · lentille {writer.Lens.Name}";

        b.AppendLine($"# Bilan {review.Week}");
        b.AppendLine();
        b.AppendLine($"Semaine du {Day(review.MondayOf)}{lens}");
        b.AppendLine();

        if (review.IsEmpty)
        {
            b.AppendLine("Aucune tâche cette semaine-là. Rien à commenter — et ce n'est pas un reproche :");
            b.AppendLine("une semaine sans session est une semaine sans session.");
            return b.ToString();
        }

        b.AppendLine($"{review.Tasks} tâche(s) · {review.Sessions} session(s) · " +
                     $"{review.ActiveDays} jour(s) actif(s) · {Minutes(review.ActiveTime)} de travail effectif");
        b.AppendLine();

        // ---- ce qui a progressé ----
        b.AppendLine("## Ce qui a progressé");
        b.AppendLine();
        if (review.Win is { } win)
        {
            b.AppendLine($"**{Capitalize(win.Statement)}.**");
            b.AppendLine();
            b.AppendLine($"`{win.SignalKey}` est passé de {Num(win.Before)} à {Num(win.After)} " +
                         $"depuis les semaines précédentes, et franchit sa cible.");
        }
        else
        {
            b.AppendLine("Rien de net cette semaine. Ce n'est pas un jugement : il faut plusieurs");
            b.AppendLine("semaines pour qu'une progression se distingue du bruit.");
        }
        b.AppendLine();

        // ---- observations ----
        b.AppendLine("## Ce que les traces montrent");
        b.AppendLine();
        if (review.Observations.Count == 0)
        {
            b.AppendLine("Aucun signal sous sa cible cette semaine.");
            b.AppendLine();
        }
        foreach (var o in review.Observations)
        {
            var term = string.IsNullOrEmpty(o.LevelTerm) ? "" : $" · {o.LevelTerm}";
            b.AppendLine($"### Palier {o.Level}{term} — {Capitalize(o.Statement)}");
            b.AppendLine();
            if (!string.IsNullOrEmpty(o.Flourish))
            {
                b.AppendLine($"*{Sentence(o.Flourish!)}*");
                b.AppendLine();
            }
            b.AppendLine($"Par exemple le {Day(o.TaskDate)}, sur **{Short(o.TaskTitle)}** : {o.Evidence}.");
            b.AppendLine();
            b.AppendLine($"**Comment franchir.** {o.Advice}");
            b.AppendLine();
            b.AppendLine($"<sub>`{o.SignalKey}` = {Num(o.Value)} en moyenne sur la semaine.</sub>");
            b.AppendLine();
        }

        // ---- usage ----
        if (review.Usage is { } u && u.TotalRead > 0)
        {
            b.AppendLine("## Consommation");
            b.AppendLine();
            b.AppendLine($"{Thousands(u.TotalRead)} jetons lus, {u.CacheRatio:P0} depuis le cache. " +
                         $"Modèles : {ModelMix(u)}.");
            b.AppendLine();
            foreach (var a in review.Alerts.Where(a => a.Severity == "attention"))
            {
                b.AppendLine($"> **{a.Title}.** {a.Detail}");
                b.AppendLine();
            }
        }

        // ---- défi ----
        b.AppendLine("## Le défi de la semaine");
        b.AppendLine();
        if (review.Challenge is { } c)
        {
            b.AppendLine($"**{c.Render()}**");
            b.AppendLine();
            b.AppendLine($"Pourquoi celui-là : {c.Why}");
            b.AppendLine();
            b.AppendLine($"On le vérifiera sur `{c.Verification}`.");
        }
        else
        {
            b.AppendLine("Aucun défi cette semaine : tous les signaux mesurables sont au-dessus de leur cible.");
        }
        b.AppendLine();
        b.AppendLine("---");
        b.AppendLine();
        b.AppendLine("<sub>Bilan produit à partir de vos transcripts locaux. Aucun texte de prompt n'en sort.</sub>");
        return b.ToString();
    }

    private static string ModelMix(WeekUsage u)
    {
        var parts = new[] { ModelFamily.Opus, ModelFamily.Sonnet, ModelFamily.Haiku }
            .Where(f => u.ShareOf(f) > 0.01)
            .Select(f => $"{f} {u.ShareOf(f):P0}");
        return parts.Any() ? string.Join(", ", parts) : "—";
    }

    private static string Day(DateOnly d)
        => $"{Days[(int)d.DayOfWeek]} {d.Day} {Months[d.Month - 1]}";
    private static string Num(double v)
        => (v >= 10 ? v.ToString("0.0") : v.ToString("0.00")).Replace('.', ',');
    private static string Minutes(TimeSpan t)
        => t.TotalMinutes >= 60 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{(int)t.TotalMinutes} min";
    /// <summary>Un titre cité doit tenir sur une ligne de lecture, pas remplir la phrase.</summary>
    private static string Short(string title)
        => title.Length <= 52 ? title : title[..51] + "…";

    /// <summary>Espace insécable fine entre les milliers, comme il se doit en français.</summary>
    private static string Thousands(long value)
    {
        var digits = Math.Abs(value).ToString();
        var b = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) b.Append('\u202f');
            b.Append(digits[i]);
        }
        return (value < 0 ? "-" : "") + b;
    }

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Une image peut tenir en trois mots comme en trois phrases : on met la
    /// majuscule, et on ne rajoute le point que s'il manque — sans quoi une
    /// scène qui se termine déjà proprement finirait par « .. ».
    /// </summary>
    internal static string Sentence(string s)
    {
        var text = Capitalize(s.Trim());
        return text.Length == 0 || ".!?…".Contains(text[^1]) ? text : text + ".";
    }
}
