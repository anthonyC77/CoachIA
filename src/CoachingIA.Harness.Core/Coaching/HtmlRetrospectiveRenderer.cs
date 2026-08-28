using System.Net;
using System.Text;
using CoachingIA.Harness.Core.Transcripts;

namespace CoachingIA.Harness.Core.Coaching;

/// <summary>
/// La rétrospective en page web. Elle réutilise la feuille de style du bilan :
/// deux pages produites par le même coach ne doivent pas avoir l'air de venir de
/// deux outils différents.
///
/// Le parti pris graphique tient en une règle : <strong>on ne dessine que ce qui
/// a été mesuré</strong>. Une semaine sans travail crée un trou dans la courbe,
/// pas un zéro — relier deux points de part et d'autre d'un mois de congé
/// raconterait une progression qui n'a jamais eu lieu.
/// </summary>
public static class HtmlRetrospectiveRenderer
{
    private const string Q = "\"";

    private static readonly string[] Months =
    [
        "janvier", "février", "mars", "avril", "mai", "juin",
        "juillet", "août", "septembre", "octobre", "novembre", "décembre",
    ];

    public static string Render(Retrospective r, LensWriter writer)
    {
        var b = new StringBuilder();
        b.Append(HtmlReviewRenderer.HeadFor($"Rétrospective {r.From:yyyy-MM} → {r.To:yyyy-MM}", Css));
        b.Append("<body>\n<main class=\"page\">\n");
        b.Append(Header(r, writer));

        if (r.IsEmpty)
        {
            b.Append("""
                <section class="card quiet">
                  <p>Aucune tâche sur la période demandée. Rien à rejouer — et ce n'est pas
                  un reproche : une période sans session est une période sans session.</p>
                </section>
                </main>
                </body>
                """);
            return b.ToString();
        }

        b.Append(Group(r, TrendVerdict.Acquis, "Ce qui est acquis",
            "Sous la cible au départ, au-dessus depuis assez longtemps pour que ce ne soit plus un hasard.", "win"));
        b.Append(Group(r, TrendVerdict.EnRecul, "Ce qui a reculé",
            "C'était mieux avant. C'est la partie la plus utile de la page : un acquis se reperd.", "alarm"));
        b.Append(Group(r, TrendVerdict.EnProgres, "Ce qui progresse",
            "La pente est bonne, la cible n'est pas encore tenue.", ""));
        b.Append(Group(r, TrendVerdict.Stable, "Ce qui n'a pas bougé",
            "Ni mieux ni moins bien depuis le début de la période. C'est ici que se trouvent les paliers qu'on ne franchit pas tout seul.", ""));
        b.Append(Milestones(r));
        b.Append(MonthTable(r, writer));
        b.Append(Silences(r));
        b.Append(TooLittle(r));

        b.Append("""
            <footer>
              <p>Rétrospective produite à partir de vos transcripts locaux. Aucun texte de prompt
              n'a quitté votre poste. Les semaines sans trace apparaissent comme des trous&nbsp;:
              elles ne comptent jamais comme une régression.</p>
            </footer>
            </main>
            </body>
            """);
        return b.ToString();
    }

    // ---------------------------------------------------------------- en-tête

    private static string Header(Retrospective r, LensWriter writer)
    {
        var lens = writer.Lens.Id == "neutre" ? ""
            : $"<span class=\"lens\">lentille {E(writer.Lens.Name)}{(writer.Side is { } s && s.Name.Length > 0 ? " · " + E(s.Name) : "")}</span>";

        var months = Math.Max(1, (r.To.Year - r.From.Year) * 12 + r.To.Month - r.From.Month + 1);
        return $"""
            <header class="masthead">
              <p class="eyebrow">Rétrospective {lens}</p>
              <h1>{months} mois de traces</h1>
              <p class="lede">Du {Day(r.From)} au {Day(r.To)} — {r.WeeksActive} semaines travaillées
              sur {r.WeeksCovered}{(r.WeeksSilent > 0 ? $", {r.WeeksSilent} sans aucune trace" : "")}.</p>
              <div class="stats">
                {Stat(r.Tasks.ToString(), "tâches")}
                {Stat(r.Sessions.ToString(), "sessions")}
                {Stat(Hours(r.ActiveTime), "de travail effectif")}
                {Stat(Thousands(r.TokensRead), "jetons lus")}
              </div>
            </header>
            """;
    }

    private static string Stat(string value, string label)
        => $"<div class=\"stat\"><span class=\"v\">{E(value)}</span><span class=\"l\">{E(label)}</span></div>";

    // ------------------------------------------------------------ trajectoires

    private static string Group(Retrospective r, TrendVerdict verdict, string title, string intro, string modifier)
    {
        var trails = r.With(verdict).ToList();
        if (trails.Count == 0) return "";

        var b = new StringBuilder();
        b.Append($"<section class=\"card {modifier}\">\n<h2>{E(title)}</h2>\n<p class=\"muted\">{E(intro)}</p>\n");
        foreach (var t in trails) b.Append(Trail(t, r, verdict));
        b.Append("</section>\n");
        return b.ToString();
    }

    private static string Trail(SignalTrail t, Retrospective r, TrendVerdict verdict)
    {
        var spec = SignalSpecs.Find(t.Key);
        var term = t.LevelTerm.Length > 0 ? t.LevelTerm : $"palier {t.Level}";

        // L'image d'un signal décrit toujours le défaut : elle n'a de sens que
        // tant que le défaut existe. La coller sous un acquis reviendrait à
        // raconter la défaite juste sous l'annonce de la victoire.
        var stillWrong = spec is not null && !spec.Meets(t.Late);
        var image = string.IsNullOrEmpty(t.Flourish) || !stillWrong ? ""
            : $"<p class=\"flourish\">{E(Sentence(t.Flourish!))}</p>\n";

        // Le conseil n'a de sens que là où il reste quelque chose à corriger.
        // Le rappeler sous un acquis transformerait une bonne nouvelle en reproche.
        var advice = verdict is TrendVerdict.Acquis ? ""
            : $"""
               <div class="advice">
                 <p class="advice-title">Pour franchir</p>
                 <p>{E(t.Advice)}</p>
               </div>
               """;

        var crossed = t.CrossedOn is { } on
            ? $"<p class=\"metric\">Franchi la semaine du {E(Day(on))}, tenu depuis.</p>"
            : "";

        return $"""
            <article class="obs trail" data-level="{t.Level}">
              <span class="chip">{E(term)}</span>
              <h3>{E(Sentence(t.Statement))}</h3>
              {image}{Spark(t, r)}
              <p class="metric"><code>{E(t.Key)}</code> · {t.Points.Count} semaines mesurées ·
              cible {E(spec is null ? "—" : RetrospectiveBuilder.Fmt(spec, spec.Target))}
              {E(spec is { HigherIsBetter: false } ? "au plus" : "au moins")}</p>
              {crossed}
              {advice}
            </article>
            """;
    }

    /// <summary>
    /// Une courbe de 200 × 44, sans bibliothèque. Les segments se coupent dès
    /// qu'il manque deux semaines : la ligne ne franchit jamais un trou.
    /// </summary>
    private static string Spark(SignalTrail t, Retrospective r)
    {
        var pts = t.Points;
        if (pts.Count < 2) return "";

        const double W = 200, H = 44, Pad = 4;

        var values = pts.Select(p => p.Value).Append(t.Target).ToList();
        var min = values.Min();
        var max = values.Max();
        if (max - min < 1e-9) { max = min + 1; }

        var span = Math.Max(1, pts[^1].MondayOf.DayNumber - pts[0].MondayOf.DayNumber);
        double X(TrailPoint p) => Pad + (p.MondayOf.DayNumber - pts[0].MondayOf.DayNumber) / (double)span * (W - 2 * Pad);
        double Y(double v) => H - Pad - (v - min) / (max - min) * (H - 2 * Pad);

        var segments = new List<List<TrailPoint>> { new() { pts[0] } };
        for (var i = 1; i < pts.Count; i++)
        {
            var weeksApart = (pts[i].MondayOf.DayNumber - pts[i - 1].MondayOf.DayNumber) / 7;
            if (weeksApart >= 3) segments.Add([]);   // trou franc : on coupe la ligne
            segments[^1].Add(pts[i]);
        }

        var stroke = t.Verdict switch
        {
            TrendVerdict.Acquis => "var(--ok)",
            TrendVerdict.EnRecul => "var(--crit)",
            TrendVerdict.EnProgres => "var(--p3)",
            _ => "var(--ink-3)",
        };

        var b = new StringBuilder();
        b.Append($"<svg class={Q}spark{Q} viewBox={Q}0 0 {W:F0} {H:F0}{Q} preserveAspectRatio={Q}none{Q} role={Q}img{Q} ");
        b.Append($"aria-label={Q}{E($"{t.Key} : {RetrospectiveBuilder.Fmt(SignalSpecs.Find(t.Key)!, t.Early)} au début, {RetrospectiveBuilder.Fmt(SignalSpecs.Find(t.Key)!, t.Late)} à la fin")}{Q}>");

        var ty = Y(t.Target);
        b.Append($"<line x1={Q}0{Q} y1={Q}{ty:F1}{Q} x2={Q}{W:F0}{Q} y2={Q}{ty:F1}{Q} class={Q}target{Q}/>");

        foreach (var seg in segments.Where(s => s.Count > 0))
        {
            if (seg.Count == 1)
            {
                b.Append($"<circle cx={Q}{X(seg[0]):F1}{Q} cy={Q}{Y(seg[0].Value):F1}{Q} r={Q}2{Q} fill={Q}{stroke}{Q}/>");
                continue;
            }
            var d = string.Join(" ", seg.Select(p => $"{X(p):F1},{Y(p.Value):F1}"));
            b.Append($"<polyline points={Q}{d}{Q} fill={Q}none{Q} stroke={Q}{stroke}{Q} stroke-width={Q}1.6{Q} vector-effect={Q}non-scaling-stroke{Q}/>");
        }

        var last = pts[^1];
        b.Append($"<circle cx={Q}{X(last):F1}{Q} cy={Q}{Y(last.Value):F1}{Q} r={Q}2.6{Q} fill={Q}{stroke}{Q}/>");
        b.Append("</svg>");

        var spec = SignalSpecs.Find(t.Key)!;
        return $"""
            <div class="sparkline">
              {b}
              <div class="spark-ends">
                <span>{E(Day(pts[0].MondayOf))} · {E(RetrospectiveBuilder.Fmt(spec, t.Early))}</span>
                <span class="dash">cible {E(RetrospectiveBuilder.Fmt(spec, spec.Target))}</span>
                <span>{E(Day(pts[^1].MondayOf))} · {E(RetrospectiveBuilder.Fmt(spec, t.Late))}</span>
              </div>
            </div>
            """;
    }

    // --------------------------------------------------------------- bascules

    private static string Milestones(Retrospective r)
    {
        if (r.Milestones.Count == 0) return "";
        var b = new StringBuilder("""
            <section class="card">
              <h2>Les bascules</h2>
              <p class="muted">Les semaines où quelque chose a changé pour de bon : la cible franchie,
              et plus jamais reperdue ensuite.</p>
              <ol class="timeline">
            """);
        foreach (var m in r.Milestones)
            b.Append($"""
                <li data-level="{m.Level}">
                  <span class="when">{E(Day(m.On))}</span>
                  <span class="what">{E(Sentence(m.Statement))}</span>
                </li>
                """);
        b.Append("</ol>\n</section>\n");
        return b.ToString();
    }

    // ------------------------------------------------------------ mois par mois

    private static string MonthTable(Retrospective r, LensWriter writer)
    {
        if (r.Months.Count == 0) return "";
        var levels = SignalSpecs.All.Select(s => s.Level).Distinct().OrderBy(l => l).ToList();

        var head = new StringBuilder("<tr><th>Mois</th><th>Tâches</th><th>Effectif</th><th>Modèles</th>");
        foreach (var level in levels)
            head.Append($"<th class={Q}lvl{Q} title={Q}{E(writer.TermFor(level, "palier " + level))}{Q}>P{level}</th>");
        head.Append("</tr>");

        var rows = new StringBuilder();
        foreach (var m in r.Months)
        {
            rows.Append($"<tr><th scope={Q}row{Q}>{E(MonthName(m.FirstDay))}</th>");
            rows.Append($"<td class={Q}num{Q}>{m.Tasks}</td>");
            rows.Append($"<td class={Q}num{Q}>{E(Hours(m.ActiveTime))}</td>");
            rows.Append($"<td class={Q}mixcell{Q}>{Mix(m)}</td>");
            foreach (var level in levels)
            {
                if (!m.LevelScores.TryGetValue(level, out var score))
                { rows.Append($"<td class={Q}score{Q}><span class={Q}none{Q}>—</span></td>"); continue; }
                rows.Append($"<td class={Q}score{Q} data-level={Q}{level}{Q}>"
                    + $"<span class={Q}bar{Q} style={Q}--h:{score * 100:F0}%{Q}></span>"
                    + $"<span class={Q}pct{Q}>{score * 100:F0}</span></td>");
            }
            rows.Append("</tr>");
        }

        return $"""
            <section class="card">
              <h2>Mois par mois</h2>
              <p class="muted">Le volume, le mélange de modèles, et la part des signaux de chaque
              palier qui tiennent leur cible. Une colonne vide veut dire « pas mesuré ce mois-là »,
              pas « zéro ».</p>
              <div class="scroll">
                <table class="months">
                  <thead>{head}</thead>
                  <tbody>{rows}</tbody>
                </table>
              </div>
            </section>
            """;
    }

    private static string Mix(MonthBand m)
    {
        var parts = new (string Name, double Share)[] { ("H", m.Haiku), ("S", m.Sonnet), ("O", m.Opus) };
        if (parts.All(p => p.Share < 0.005)) return "<span class=\"none\">—</span>";
        var b = new StringBuilder("<span class=\"minimix\">");
        foreach (var (name, share) in parts)
            b.Append($"<span class={Q}seg {name}{Q} style={Q}--w:{share * 100:F0}%{Q} title={Q}{name} {share * 100:F0} %{Q}></span>");
        b.Append("</span>");
        b.Append($"<span class={Q}mixlabel{Q}>{m.Haiku * 100:F0}/{m.Sonnet * 100:F0}/{m.Opus * 100:F0}</span>");
        return b.ToString();
    }

    // --------------------------------------------------------------- silences

    private static string Silences(Retrospective r)
    {
        if (r.Silences.Count == 0) return "";
        var b = new StringBuilder("""
            <section class="card quiet">
              <h2>Les périodes sans trace</h2>
              <p class="muted">Elles sont signalées parce qu'elles expliquent des courbes, pas parce
              qu'elles sont un reproche. Une reprise après trois semaines d'arrêt repart rarement
              au niveau où elle s'était arrêtée&nbsp;: c'est normal, et c'est utile à savoir.</p>
              <ul class="plain">
            """);
        foreach (var (from, to, weeks) in r.Silences)
            b.Append($"<li>{weeks} semaines sans session, du {E(Day(from))} au {E(Day(to))}.</li>");
        b.Append("</ul>\n</section>\n");
        return b.ToString();
    }

    private static string TooLittle(Retrospective r)
    {
        var trails = r.With(TrendVerdict.TropPeuDeDonnees).ToList();
        if (trails.Count == 0) return "";
        var keys = string.Join(", ", trails.Select(t => $"<code>{E(t.Key)}</code>"));
        return $"""
            <section class="card quiet">
              <h2>Pas encore assez mesuré</h2>
              <p class="muted">{keys} — moins de trois semaines de mesure. Le coach ne dira rien
              tant qu'il ne peut pas distinguer une tendance d'un accident.</p>
            </section>
            """;
    }

    // ----------------------------------------------------------------- outils

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Sentence(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return t;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return ".!?…".Contains(t[^1]) ? t : t + ".";
    }

    private static string Day(DateOnly d) => $"{d.Day} {Months[d.Month - 1]} {d.Year}";
    private static string MonthName(DateOnly d) => $"{Months[d.Month - 1]} {d.Year}";

    private static string Hours(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{(int)t.TotalMinutes} min";

    private static string Thousands(long value)
    {
        var s = Math.Abs(value).ToString("0");
        var b = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && (s.Length - i) % 3 == 0) b.Append(' ');
            b.Append(s[i]);
        }
        return (value < 0 ? "-" : "") + b;
    }

    private const string Css = """

        /* ---- rétrospective ---- */
        .trail{padding-top:22px}
        .sparkline{margin:14px 0 6px}
        .spark{width:100%;height:44px;display:block;background:var(--sunk);
          border:1px solid var(--line);padding:0}
        .spark .target{stroke:var(--line-2);stroke-width:1;stroke-dasharray:3 3;vector-effect:non-scaling-stroke}
        .spark-ends{display:flex;justify-content:space-between;gap:10px;margin-top:5px;
          font-family:"IBM Plex Mono",monospace;font-size:.72rem;color:var(--ink-3)}
        .spark-ends .dash{color:var(--line-2)}
        .alarm{border-left:3px solid var(--crit)}

        .timeline{list-style:none;padding:0;margin:14px 0 0;display:flex;flex-direction:column;gap:0}
        .timeline li{display:grid;grid-template-columns:150px 1fr;gap:16px;padding:12px 0 12px 14px;
          border-left:2px solid var(--line);position:relative}
        .timeline li::before{content:"";position:absolute;left:-5px;top:20px;width:8px;height:8px;
          border-radius:50%;background:var(--ink-3)}
        .timeline li[data-level="1"]::before{background:var(--p1)}
        .timeline li[data-level="2"]::before{background:var(--p2)}
        .timeline li[data-level="3"]::before{background:var(--p3)}
        .timeline li[data-level="4"]::before{background:var(--p4)}
        .timeline li[data-level="5"]::before{background:var(--p5)}
        .timeline .when{font-family:"IBM Plex Mono",monospace;font-size:.78rem;color:var(--ink-3);
          padding-top:.2em}
        .timeline .what{font-weight:600}

        .scroll{overflow-x:auto;margin-top:16px}
        table.months{border-collapse:collapse;width:100%;font-size:.9rem}
        table.months th,table.months td{border-bottom:1px solid var(--line);padding:9px 10px;
          text-align:left;white-space:nowrap}
        table.months thead th{font-family:"IBM Plex Mono",monospace;font-size:10.5px;
          letter-spacing:.1em;text-transform:uppercase;color:var(--ink-3);font-weight:500}
        table.months tbody th{font-weight:600}
        table.months .num{font-variant-numeric:tabular-nums;text-align:right}
        table.months .none{color:var(--ink-3)}
        .minimix{display:inline-flex;width:96px;height:8px;background:var(--sunk);
          border-radius:2px;overflow:hidden;vertical-align:middle}
        .minimix .seg{width:var(--w)}
        .minimix .H{background:var(--p1)} .minimix .S{background:var(--p3)} .minimix .O{background:var(--p5)}
        .mixlabel{font-family:"IBM Plex Mono",monospace;font-size:.72rem;color:var(--ink-3);margin-left:8px}
        td.score{text-align:center;width:46px}
        td.score .bar{display:block;height:6px;width:var(--h);min-width:2px;margin:0 auto 3px;
          background:var(--ink-3);border-radius:2px}
        td.score[data-level="1"] .bar{background:var(--p1)} td.score[data-level="2"] .bar{background:var(--p2)}
        td.score[data-level="3"] .bar{background:var(--p3)} td.score[data-level="4"] .bar{background:var(--p4)}
        td.score[data-level="5"] .bar{background:var(--p5)}
        td.score .pct{font-family:"IBM Plex Mono",monospace;font-size:.72rem;color:var(--ink-3);
          font-variant-numeric:tabular-nums}
        ul.plain{margin:12px 0 0;padding-left:18px}
        ul.plain li{margin-bottom:6px}
        @media (max-width:640px){.timeline li{grid-template-columns:1fr;gap:2px}}
        """;
}
