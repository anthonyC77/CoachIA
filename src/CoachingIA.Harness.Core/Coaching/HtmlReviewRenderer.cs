using System.Net;
using System.Text;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// Le bilan en page web autonome : un seul fichier, aucune dépendance, ouvrable
/// d'un double-clic. La console convient pour vérifier qu'un calcul tombe juste ;
/// elle ne convient pas pour lire côte à côte le prompt qu'on a écrit et celui
/// qu'on aurait pu écrire — et c'est cette comparaison qui fait tout l'intérêt
/// du bilan.
/// </summary>
public static class HtmlReviewRenderer
{
    private static readonly string[] Months =
    [
        "janvier", "février", "mars", "avril", "mai", "juin",
        "juillet", "août", "septembre", "octobre", "novembre", "décembre",
    ];
    private static readonly string[] DayNames =
    [
        "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi",
    ];

    public static string Render(WeeklyReview r, LensWriter writer)
    {
        var b = new StringBuilder();
        b.Append(Head(r));
        b.Append("<body>\n<main class=\"page\">\n");
        b.Append(Header(r, writer));

        if (r.IsEmpty)
        {
            b.Append("""
                <section class="card quiet">
                  <p>Aucune tâche cette semaine-là. Rien à commenter — et ce n'est pas un reproche :
                  une semaine sans session est une semaine sans session.</p>
                </section>
                """);
            b.Append("</main>\n</body>");
            return b.ToString();
        }

        b.Append(Win(r));
        b.Append(Prompt(r));
        b.Append(Observations(r));
        b.Append(Usage(r));
        b.Append(Challenge(r));
        b.Append("""
            <footer>
              <p>Bilan produit à partir de vos transcripts locaux. Aucun texte de prompt n'a quitté votre poste.</p>
            </footer>
            </main>
            </body>
            """);
        return b.ToString();
    }

    // ---------------------------------------------------------------- en-tête

    private static string Header(WeeklyReview r, LensWriter writer)
    {
        var lens = writer.Lens.Id == "neutre" ? "" :
            $"<span class=\"lens\">lentille {E(writer.Lens.Name)}</span>";
        return $"""
            <header class="masthead">
              <p class="eyebrow">Bilan hebdomadaire {lens}</p>
              <h1>{E(r.Week)}</h1>
              <p class="lede">Semaine du {Day(r.MondayOf)} au {Day(r.MondayOf.AddDays(6))}</p>
              <div class="stats">
                {Stat(r.Tasks.ToString(), "tâches")}
                {Stat(r.Sessions.ToString(), "sessions")}
                {Stat(r.ActiveDays.ToString(), r.ActiveDays > 1 ? "jours actifs" : "jour actif")}
                {Stat(Minutes(r.ActiveTime), "de travail effectif")}
              </div>
            </header>
            """;
    }

    private static string Stat(string value, string label)
        => $"<div class=\"stat\"><span class=\"v\">{E(value)}</span><span class=\"l\">{E(label)}</span></div>";

    // -------------------------------------------------------------- progression

    private static string Win(WeeklyReview r)
    {
        if (r.Win is not { } w)
            return """
                <section class="card">
                  <h2>Ce qui a progressé</h2>
                  <p class="muted">Rien de net cette semaine. Ce n'est pas un jugement : il faut
                  plusieurs semaines pour qu'une progression se distingue du bruit.</p>
                </section>
                """;

        return $"""
            <section class="card win">
              <h2>Ce qui a progressé</h2>
              <p class="headline">{E(Capitalize(w.Statement))}.</p>
              <p class="muted"><code>{E(w.SignalKey)}</code> est passé de {Num(w.Before)} à {Num(w.After)}
              depuis les semaines précédentes, et franchit sa cible.</p>
            </section>
            """;
    }

    // ------------------------------------------------------- prompt de la semaine

    private static string Prompt(WeeklyReview r)
    {
        if (r.PromptOfTheWeek is not { } c || r.PromptContext is not { } ctx) return "";

        var checklist = new StringBuilder();
        foreach (var item in PromptRubric.Items)
        {
            var ok = c.Present.Any(p => p.Key == item.Key);
            var state = ok ? "ok" : "miss";
            var mark = ok ? "\u2713" : "\u2717";
            var fix = ok ? "" : "<p class=" + Q + "fix" + Q + ">" + E(item.Fix) + "</p>";
            checklist.Append($"""
                <li class="{state}">
                  <span class="mark" aria-hidden="true">{mark}</span>
                  <div>
                    <p class="label">{E(item.Label)}</p>
                    {fix}
                  </div>
                </li>
                """);
        }

        var origin = c.Source == "claude -p"
            ? "Réécriture proposée"
            : "Structure manquante";
        var note = c.Source == "claude -p"
            ? ""
            : """
              <p class="muted note">Les crochets marquent ce qu'il faut compléter : la version
              hors ligne dessine la forme, elle ne rédige pas à votre place. Installez le CLI
              Claude et relancez pour obtenir la réécriture.</p>
              """;

        return $"""
            <section class="card prompt">
              <h2>Le prompt de la semaine</h2>
              <p class="muted">Le plus coûteux des {r.Tasks} de la semaine : <strong>{E(ctx.Title)}</strong>,
              le {Day(ctx.Date)}. Ce qu'il a coûté : {E(HeuristicCost(ctx))}.</p>

              <div class="compare">
                <div class="side before">
                  <p class="side-title">Vous avez écrit</p>
                  <pre>{E(c.Original.Trim())}</pre>
                </div>
                <div class="side after">
                  <p class="side-title">{origin}</p>
                  <pre>{E(c.Rewrite)}</pre>
                </div>
              </div>
              {note}

              <h3>Ce qui manquait</h3>
              <ul class="rubric">{checklist}</ul>
            </section>
            """;
    }

    private static string HeuristicCost(PromptContext c) => HeuristicPromptCritic.Cost(c);

    // ------------------------------------------------------------ observations

    private static string Observations(WeeklyReview r)
    {
        if (r.Observations.Count == 0)
            return """
                <section class="card">
                  <h2>Ce que les traces montrent</h2>
                  <p class="muted">Aucun signal sous sa cible cette semaine.</p>
                </section>
                """;

        var b = new StringBuilder("<section class=\"card\">\n<h2>Ce que les traces montrent</h2>\n");
        foreach (var o in r.Observations)
        {
            var term = string.IsNullOrEmpty(o.LevelTerm) ? "" : " · " + E(o.LevelTerm);
            var image = string.IsNullOrEmpty(o.Flourish)
                ? ""
                : "<p class=" + Q + "flourish" + Q + ">" + E(Sentence(o.Flourish!)) + "</p>";
            b.Append($"""
                <article class="obs" data-level="{o.Level}">
                  <p class="chip">Palier {o.Level}{term}</p>
                  <h3>{E(Capitalize(o.Statement))}</h3>
                  {image}
                  <p class="example">Le {Day(o.TaskDate)}, sur <strong>{E(Short(o.TaskTitle))}</strong> :
                  {E(o.Evidence)}.</p>
                  <div class="advice">
                    <p class="advice-title">Comment franchir</p>
                    <p>{E(o.Advice)}</p>
                  </div>
                  <p class="metric"><code>{E(o.SignalKey)}</code> = {Num(o.Value)} en moyenne sur la semaine</p>
                </article>
                """);
        }
        b.Append("</section>\n");
        return b.ToString();
    }

    // -------------------------------------------------------------- consommation

    private static string Usage(WeeklyReview r)
    {
        if (r.Usage is not { } u || u.TotalRead == 0) return "";

        var bars = new StringBuilder();
        foreach (var f in new[] { ModelFamily.Opus, ModelFamily.Sonnet, ModelFamily.Haiku })
        {
            var share = u.ShareOf(f);
            if (share <= 0.005) continue;
            bars.Append($"""
                <div class="mix-row">
                  <span class="mix-name">{f}</span>
                  <span class="mix-track"><span class="mix-fill" style="width:{share * 100:F0}%"></span></span>
                  <span class="mix-val">{share:P0}</span>
                </div>
                """);
        }

        var alerts = new StringBuilder();
        foreach (var a in r.Alerts.Where(a => a.Severity == "attention"))
            alerts.Append($"<p class=\"alert\"><strong>{E(a.Title)}.</strong> {E(a.Detail)}</p>");

        return $"""
            <section class="card">
              <h2>Consommation</h2>
              <p><strong>{Thousands(u.TotalRead)}</strong> jetons lus, dont {u.CacheRatio:P0} depuis le cache.</p>
              <div class="mix">{bars}</div>
              {alerts}
            </section>
            """;
    }

    // --------------------------------------------------------------------- défi

    private static string Challenge(WeeklyReview r)
    {
        if (r.Challenge is not { } c)
            return """
                <section class="card">
                  <h2>Le défi de la semaine</h2>
                  <p class="muted">Aucun défi : tous les signaux mesurables sont au-dessus de leur cible.</p>
                </section>
                """;

        return $"""
            <section class="card challenge">
              <h2>Le défi de la semaine</h2>
              <p class="headline">{E(c.Render())}</p>
              <p class="muted">Pourquoi celui-là : {E(c.Why)}</p>
              <p class="verif">On le vérifiera sur <code>{E(c.Verification)}</code>.</p>
            </section>
            """;
    }

    // ----------------------------------------------------------------- utilitaires

    private const string Q = "\"";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Day(DateOnly d) => $"{DayNames[(int)d.DayOfWeek]} {d.Day} {Months[d.Month - 1]}";
    private static string Short(string t) => t.Length <= 60 ? t : t[..59] + "…";
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Majuscule, et le point seulement s'il manque — une scène peut faire trois phrases.</summary>
    internal static string Sentence(string s)
    {
        var text = Capitalize(s.Trim());
        return text.Length == 0 || ".!?…".Contains(text[^1]) ? text : text + ".";
    }
    private static string Num(double v) => (v >= 10 ? v.ToString("0.0") : v.ToString("0.00")).Replace('.', ',');
    private static string Minutes(TimeSpan t)
        => t.TotalMinutes >= 60 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{(int)t.TotalMinutes} min";

    private static string Thousands(long value)
    {
        var digits = Math.Abs(value).ToString();
        var b = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) b.Append(' ');
            b.Append(digits[i]);
        }
        return (value < 0 ? "-" : "") + b;
    }

    private static string Head(WeeklyReview r) => HeadFor("Bilan " + r.Week);

    /// <summary>
    /// L'en-tête et la feuille de style, partagés par toutes les pages du coach.
    /// Une seule palette, un seul jeu de composants : deux pages produites par le
    /// même outil ne doivent pas avoir l'air de venir de deux outils différents.
    /// </summary>
    internal static string HeadFor(string title, string extraCss = "")
    {
        var head = HeadTop + E(title) + HeadRest;
        return extraCss.Length == 0 ? head : head.Replace("</style>", extraCss + "\n</style>");
    }

    private const string HeadTop = """
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>
        """;

    private const string HeadRest = """
         · CoachingIA</title>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,600;12..96,700&family=IBM+Plex+Mono:wght@400;500&family=IBM+Plex+Sans:wght@400;500;600&display=swap">
        <style>
        :root{
          --paper:#F3F6F7; --surface:#FFFFFF; --sunk:#EAEEF0; --line:#D7DEE0; --line-2:#BCC7CA;
          --ink:#111819; --ink-2:#47565A; --ink-3:#79878C;
          --p1:#10B195; --p2:#0097B2; --p3:#1172BD; --p4:#5648AA; --p5:#6D1C79;
          --accent:#C24A16; --ok:#2E7150; --warn:#8A5A0E; --crit:#A93727;
        }
        @media (prefers-color-scheme: dark){:root:not([data-theme="light"]){
          --paper:#0C1113; --surface:#141A1D; --sunk:#0A0F11; --line:#232D31; --line-2:#38474C;
          --ink:#E5ECEE; --ink-2:#A6B4B9; --ink-3:#78868B;
          --p1:#3EA692; --p2:#48B2C6; --p3:#82B5EF; --p4:#B3B2FB; --p5:#DDAFEC;
          --accent:#F0793E; --ok:#4CA277; --warn:#C08F35; --crit:#DB6A5B;
        }}
        :root[data-theme="dark"]{
          --paper:#0C1113; --surface:#141A1D; --sunk:#0A0F11; --line:#232D31; --line-2:#38474C;
          --ink:#E5ECEE; --ink-2:#A6B4B9; --ink-3:#78868B;
          --p1:#3EA692; --p2:#48B2C6; --p3:#82B5EF; --p4:#B3B2FB; --p5:#DDAFEC;
          --accent:#F0793E; --ok:#4CA277; --warn:#C08F35; --crit:#DB6A5B;
        }
        *{box-sizing:border-box}
        body{margin:0;background:var(--paper);color:var(--ink);
          font-family:"IBM Plex Sans","Segoe UI",system-ui,sans-serif;font-size:16px;line-height:1.62;
          -webkit-font-smoothing:antialiased}
        .page{max-width:940px;margin:0 auto;padding:0 clamp(18px,4vw,40px) 80px}
        h1,h2,h3{font-family:"Bricolage Grotesque","Segoe UI",sans-serif;font-weight:700;
          margin:0;text-wrap:balance;letter-spacing:-.015em}
        code,pre,.mono{font-family:"IBM Plex Mono",ui-monospace,monospace}
        p{margin:0 0 .9em}
        .muted{color:var(--ink-2)}
        .eyebrow{font-family:"IBM Plex Mono",monospace;font-size:11px;letter-spacing:.14em;
          text-transform:uppercase;color:var(--ink-3);margin:0 0 6px}
        .lens{color:var(--accent);margin-left:8px}

        .masthead{padding:clamp(40px,7vw,72px) 0 28px;border-bottom:1px solid var(--line)}
        .masthead h1{font-size:clamp(2.2rem,6vw,3.4rem);line-height:1.02}
        .lede{color:var(--ink-2);font-size:1.05rem;margin-top:.4em}
        .stats{display:flex;flex-wrap:wrap;gap:10px 34px;margin-top:26px}
        .stat{display:flex;flex-direction:column}
        .stat .v{font-family:"Bricolage Grotesque",sans-serif;font-size:1.7rem;font-weight:700;line-height:1.1}
        .stat .l{font-size:12px;color:var(--ink-3)}

        .card{background:var(--surface);border:1px solid var(--line);padding:26px clamp(18px,3vw,30px);
          margin:26px 0}
        .card h2{font-size:1.3rem;margin-bottom:.7em}
        .card h3{font-size:1.02rem;margin:1.6em 0 .5em}
        .headline{font-size:1.12rem;font-weight:600;color:var(--ink)}
        .win{border-left:3px solid var(--ok)}
        .challenge{border-left:3px solid var(--accent)}
        .quiet{color:var(--ink-2)}
        .verif{font-size:.9rem;color:var(--ink-3)}
        code{background:var(--sunk);padding:.1em .4em;border-radius:3px;font-size:.86em;
          overflow-wrap:anywhere}

        /* ---- prompt avant / après ---- */
        .compare{display:grid;grid-template-columns:1fr 1fr;gap:1px;background:var(--line);
          border:1px solid var(--line);margin:18px 0}
        .side{background:var(--surface);padding:16px 16px 18px;min-width:0}
        .side-title{font-family:"IBM Plex Mono",monospace;font-size:10.5px;letter-spacing:.12em;
          text-transform:uppercase;margin:0 0 10px}
        .before .side-title{color:var(--ink-3)}
        .after .side-title{color:var(--accent)}
        .side pre{margin:0;white-space:pre-wrap;overflow-wrap:anywhere;font-size:.85rem;
          line-height:1.6;color:var(--ink)}
        .before pre{color:var(--ink-2)}
        .after{background:color-mix(in srgb, var(--accent) 4%, var(--surface))}
        .note{font-size:.88rem;margin-top:12px}

        .rubric{list-style:none;padding:0;margin:0;display:flex;flex-direction:column;gap:10px}
        .rubric li{display:grid;grid-template-columns:22px 1fr;gap:10px;align-items:start}
        .rubric .mark{font-weight:700;line-height:1.5}
        .rubric .ok .mark,.rubric li.ok .mark{color:var(--ok)}
        .rubric li.miss .mark{color:var(--crit)}
        .rubric .label{margin:0;font-weight:600;font-size:.95rem}
        .rubric li.ok .label{color:var(--ink-3);font-weight:500}
        .rubric .fix{margin:2px 0 0;font-size:.9rem;color:var(--ink-2)}

        /* ---- observations ---- */
        .obs{border-top:1px solid var(--line);padding-top:20px;margin-top:20px}
        .obs:first-of-type{border-top:0;padding-top:0;margin-top:0}
        .chip{display:inline-block;font-family:"IBM Plex Mono",monospace;font-size:10.5px;
          letter-spacing:.1em;text-transform:uppercase;padding:2px 9px;border-radius:20px;
          color:#fff;margin:0 0 8px}
        .obs[data-level="1"] .chip{background:var(--p1)} .obs[data-level="2"] .chip{background:var(--p2)}
        .obs[data-level="3"] .chip{background:var(--p3)} .obs[data-level="4"] .chip{background:var(--p4)}
        .obs[data-level="5"] .chip{background:var(--p5)}
        .obs h3{margin:0 0 .5em;font-size:1.06rem}
        .example{color:var(--ink-2);font-size:.96rem}
        .flourish{color:var(--accent);font-size:.95rem;font-style:italic;margin:-.4em 0 .8em}
        .advice{border-left:2px solid var(--accent);padding:2px 0 2px 14px;margin:14px 0}
        .advice-title{font-family:"IBM Plex Mono",monospace;font-size:10.5px;letter-spacing:.12em;
          text-transform:uppercase;color:var(--accent);margin:0 0 4px}
        .advice p:last-child{margin:0;font-size:.96rem}
        .metric{font-family:"IBM Plex Mono",monospace;font-size:.78rem;color:var(--ink-3);margin:10px 0 0}

        /* ---- consommation ---- */
        .mix{display:flex;flex-direction:column;gap:7px;margin:14px 0}
        .mix-row{display:grid;grid-template-columns:70px 1fr 48px;align-items:center;gap:12px}
        .mix-name{font-size:.88rem;color:var(--ink-2)}
        .mix-track{height:8px;background:var(--sunk);border-radius:2px;overflow:hidden}
        .mix-fill{display:block;height:100%;background:var(--p3);border-radius:2px}
        .mix-val{font-family:"IBM Plex Mono",monospace;font-size:.82rem;color:var(--ink-3);
          text-align:right;font-variant-numeric:tabular-nums}
        .alert{border-left:2px solid var(--warn);padding-left:14px;color:var(--ink-2);font-size:.95rem}

        footer{margin-top:46px;padding-top:22px;border-top:1px solid var(--line);
          font-size:.85rem;color:var(--ink-3)}

        @media (max-width:760px){.compare{grid-template-columns:1fr}}
        @media print{body{background:#fff}.card{break-inside:avoid}}
        </style>
        </head>
        """;
}
