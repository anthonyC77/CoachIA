using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CoachingIA.Harness.Core.Coaching;

/// <summary>
/// La console locale : une page web sommaire qui lance les mêmes commandes que
/// le terminal, sur la même machine, sans rien envoyer nulle part.
///
/// Pourquoi elle existe : le coach doit servir à quelqu'un qui n'a pas envie de
/// retenir huit commandes et leurs options. Taper
/// <c>dotnet run --project … -- bilan --lens starcraft2 --race zerg</c> une fois
/// par semaine, personne ne le fait. Cliquer sur « Bilan », si.
///
/// Trois précautions, parce qu'un serveur qui exécute des commandes mérite
/// mieux qu'une bonne intention :
///
///   1. <strong>Écoute uniquement sur 127.0.0.1.</strong> Rien n'est joignable
///      depuis le réseau, même par erreur.
///   2. <strong>Liste blanche.</strong> Seules les commandes et les options
///      connues passent, et les arguments partent par
///      <see cref="ProcessStartInfo.ArgumentList"/> : il n'y a pas de ligne de
///      commande à échapper, donc rien à y injecter.
///   3. <strong>Jeton de session.</strong> Une page ouverte ailleurs dans le
///      navigateur ne peut pas déclencher une exécution chez vous : le jeton est
///      tiré au démarrage et vérifié à chaque appel.
/// </summary>
public static class WebConsole
{
    private const string Q = "\"";

    private static readonly string[] Commands =
        ["probe", "analyze", "segment", "usage", "team", "lens", "defi", "moment", "bilan", "retro"];

    private static readonly string[] Options =
        ["--root", "--out", "--lenses", "--lens", "--race", "--week", "--limit", "--weeks",
         "--budget", "--csv", "--mois", "--depuis", "--jusqua"];

    private static readonly string[] Flags = ["--juge"];

    private sealed record Defaults(string Root, string Out, string LensDir);

    public static int Serve(int port, string? root, string? outDir, string? lensDir)
    {
        var defaults = new Defaults(
            root ?? "",
            Path.GetFullPath(outDir ?? "bilans"),
            lensDir ?? "");

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var prefix = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"  Impossible d'écouter sur {prefix} : {ex.Message}");
            Console.Error.WriteLine("  Le port est peut-être déjà pris. Essayez : coachingia web --port 5100");
            return 1;
        }

        var url = $"{prefix}?t={token}";
        Console.WriteLine($"""

              CoachingIA — console locale

              {url}

              Ouvrez ce lien dans votre navigateur. Le serveur n'écoute que sur cette
              machine, et rien ne sort de votre poste. Ctrl+C pour arrêter.
            """);
        TryOpenBrowser(url);

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            try { Handle(ctx, token, defaults); }
            catch (Exception ex)
            {
                // Une requête qui tombe ne doit pas emporter la console : on
                // répond une erreur lisible et on continue d'écouter.
                TryWrite(ctx, 500, "text/plain; charset=utf-8", "Erreur : " + ex.Message);
            }
        }
        return 0;
    }

    // ------------------------------------------------------------- routage

    private static void Handle(HttpListenerContext ctx, string token, Defaults defaults)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var query = ctx.Request.QueryString;

        if (path is "/" or "/index.html")
        {
            // La page elle-même est servie sans jeton — sinon on ne pourrait pas
            // l'ouvrir. C'est l'exécution qui est protégée, pas la lecture.
            TryWrite(ctx, 200, "text/html; charset=utf-8", Page(defaults, query["t"] ?? token));
            return;
        }

        // Tout le reste exige le jeton, et une requête venue d'ailleurs est
        // refusée même si elle l'a deviné.
        if (!string.Equals(query["t"], token, StringComparison.Ordinal))
        { TryWrite(ctx, 403, "text/plain; charset=utf-8", "Jeton absent ou invalide."); return; }

        var origin = ctx.Request.Headers["Origin"];
        if (origin is not null && !origin.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
        { TryWrite(ctx, 403, "text/plain; charset=utf-8", "Origine refusée."); return; }

        switch (path)
        {
            case "/run":
                Run(ctx, defaults);
                return;

            case "/fichier":
                ServeFile(ctx, defaults, query["p"]);
                return;

            case "/pages":
                TryWrite(ctx, 200, "application/json; charset=utf-8", PagesJson(defaults));
                return;

            default:
                TryWrite(ctx, 404, "text/plain; charset=utf-8", "Inconnu.");
                return;
        }
    }

    // ------------------------------------------------------------ exécution

    private static void Run(HttpListenerContext ctx, Defaults defaults)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = reader.ReadToEnd();

        var request = JsonSerializer.Deserialize<RunRequest>(body, Json);
        if (request is null || !Commands.Contains(request.Command, StringComparer.Ordinal))
        { TryWrite(ctx, 400, "text/plain; charset=utf-8", "Commande inconnue."); return; }

        var argv = new List<string> { request.Command };
        foreach (var (key, value) in request.Options ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (Flags.Contains(key, StringComparer.Ordinal)) { argv.Add(key); continue; }
            if (!Options.Contains(key, StringComparer.Ordinal)) continue;
            argv.Add(key);
            argv.Add(value.Trim());
        }

        var started = Stopwatch.StartNew();
        var (code, output) = Execute(argv, defaults);

        var payload = JsonSerializer.Serialize(new RunResult(
            string.Join(' ', argv),
            code,
            output,
            (int)started.ElapsedMilliseconds,
            Pages(defaults)), Json);

        TryWrite(ctx, 200, "application/json; charset=utf-8", payload);
    }

    /// <summary>
    /// Relance le même binaire en sous-processus. Les arguments partent en liste,
    /// jamais en chaîne : il n'y a donc aucune ligne de commande à échapper.
    /// </summary>
    private static (int Code, string Output) Execute(List<string> argv, Defaults defaults)
    {
        var self = Environment.ProcessPath;
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        // Lancé par « dotnet run », le processus courant est dotnet lui-même :
        // on lui repasse l'assembly plutôt que de nous relancer en boucle.
        var name = Path.GetFileNameWithoutExtension(self) ?? "";
        if (self is null || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = self ?? "dotnet";
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,
                Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]) + ".dll"));
        }
        else
        {
            info.FileName = self;
        }
        foreach (var a in argv) info.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, "Le processus n'a pas démarré.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* déjà mort */ }
                return (-1, stdout + "\n\nInterrompu : la commande a dépassé dix minutes.");
            }

            var text = stdout;
            if (stderr.Trim().Length > 0) text += "\n" + stderr;
            return (process.ExitCode, text.Trim().Length == 0 ? "(aucune sortie)" : text);
        }
        catch (Exception ex)
        {
            return (-1, "Impossible de lancer la commande : " + ex.Message);
        }
    }

    // ----------------------------------------------------- pages produites

    private static List<string> Pages(Defaults defaults)
    {
        if (!Directory.Exists(defaults.Out)) return [];
        return Directory.EnumerateFiles(defaults.Out, "*.html")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Take(20)
            .ToList();
    }

    private static string PagesJson(Defaults defaults)
        => JsonSerializer.Serialize(Pages(defaults), Json);

    /// <summary>
    /// Sert une page produite. Le chemin demandé est réduit à un nom de fichier
    /// et recollé au dossier de sortie : on ne peut pas remonter ailleurs sur le
    /// disque, même en insistant.
    /// </summary>
    private static void ServeFile(HttpListenerContext ctx, Defaults defaults, string? name)
    {
        var safe = Path.GetFileName(name ?? "");
        if (safe.Length == 0 || Path.GetExtension(safe) is not (".html" or ".md"))
        { TryWrite(ctx, 400, "text/plain; charset=utf-8", "Fichier refusé."); return; }

        var full = Path.Combine(defaults.Out, safe);
        if (!File.Exists(full))
        { TryWrite(ctx, 404, "text/plain; charset=utf-8", "Pas encore produit."); return; }

        var type = Path.GetExtension(safe) == ".html" ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
        TryWrite(ctx, 200, type, File.ReadAllText(full));
    }

    // ---------------------------------------------------------------- page

    private static string Page(Defaults defaults, string token)
    {
        var catalog = LensCatalog.Load(defaults.LensDir.Length > 0 ? defaults.LensDir : null);

        var lenses = new StringBuilder();
        foreach (var lens in catalog.All.OrderBy(l => l.Id, StringComparer.Ordinal))
            lenses.Append($"<option value={Q}{E(lens.Id)}{Q}>{E(lens.Name)}</option>");

        var races = new StringBuilder("<option value=\"\">— aucun camp —</option>");
        foreach (var lens in catalog.All)
            foreach (var (id, race) in lens.Races)
                races.Append($"<option value={Q}{E(id)}{Q} data-lens={Q}{E(lens.Id)}{Q}>"
                    + $"{E(race.Name.Length > 0 ? race.Name : id)}</option>");

        return HtmlShell
            .Replace("__TOKEN__", E(token))
            .Replace("__ROOT__", E(defaults.Root))
            .Replace("__OUT__", E(defaults.Out))
            .Replace("__LENSDIR__", E(defaults.LensDir))
            .Replace("__LENSES__", lenses.ToString())
            .Replace("__RACES__", races.ToString());
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
                return;
            }

            // Ailleurs, l'ouvreur bavarde sur stderr quand aucun navigateur n'est
            // installé : on le fait taire, le lien est déjà affiché au-dessus.
            var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(url);
            Process.Start(info);
        }
        catch
        {
            // Pas de navigateur joignable : le lien est déjà affiché, ça suffit.
        }
    }

    private static void TryWrite(HttpListenerContext ctx, int status, string type, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch (HttpListenerException) { /* le navigateur est parti */ }
        catch (ObjectDisposedException) { /* réponse déjà close */ }
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record RunRequest(string Command, Dictionary<string, string>? Options);
    private sealed record RunResult(string Line, int Code, string Output, int Ms, List<string> Pages);

    // Écrite à la main plutôt que servie depuis un fichier : la console doit
    // marcher depuis un exe déposé seul dans un dossier, sans rien à côté.
    private const string HtmlShell = """
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>CoachingIA · console locale</title>
        <style>
        :root{
          --paper:#F3F6F7; --surface:#FFFFFF; --sunk:#EAEEF0; --line:#D7DEE0; --line-2:#BCC7CA;
          --ink:#111819; --ink-2:#47565A; --ink-3:#79878C;
          --p1:#10B195; --p3:#1172BD; --accent:#C24A16; --ok:#2E7150; --crit:#A93727;
        }
        @media (prefers-color-scheme: dark){:root{
          --paper:#0C1113; --surface:#141A1D; --sunk:#0A0F11; --line:#232D31; --line-2:#38474C;
          --ink:#E5ECEE; --ink-2:#A6B4B9; --ink-3:#78868B;
          --p1:#3EA692; --p3:#82B5EF; --accent:#F0793E; --ok:#4CA277; --crit:#DB6A5B;
        }}
        *{box-sizing:border-box}
        body{margin:0;background:var(--paper);color:var(--ink);font-size:15px;line-height:1.6;
          font-family:"Segoe UI",system-ui,-apple-system,sans-serif}
        .page{max-width:1020px;margin:0 auto;padding:0 22px 70px}
        header{padding:38px 0 20px;border-bottom:1px solid var(--line)}
        h1{margin:0;font-size:1.7rem;letter-spacing:-.02em}
        .lede{color:var(--ink-2);margin:.4em 0 0}
        h2{font-size:1.02rem;margin:0 0 12px}
        .card{background:var(--surface);border:1px solid var(--line);padding:20px;margin:20px 0}
        .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:14px}
        label{display:block;font-size:11px;letter-spacing:.1em;text-transform:uppercase;
          color:var(--ink-3);margin-bottom:5px}
        input,select{width:100%;padding:8px 10px;background:var(--sunk);color:var(--ink);
          border:1px solid var(--line);border-radius:3px;font:inherit;font-size:.92rem}
        .row{display:flex;flex-wrap:wrap;gap:9px;margin-top:6px}
        button{padding:9px 15px;border:1px solid var(--line-2);background:var(--surface);
          color:var(--ink);border-radius:3px;font:inherit;font-size:.92rem;cursor:pointer}
        button:hover{border-color:var(--accent);color:var(--accent)}
        button.primary{background:var(--accent);border-color:var(--accent);color:#fff;font-weight:600}
        button.primary:hover{color:#fff;filter:brightness(1.08)}
        button:disabled{opacity:.5;cursor:progress}
        .hint{color:var(--ink-3);font-size:.86rem;margin:10px 0 0}
        .cmdline{font-family:ui-monospace,"Cascadia Mono",Consolas,monospace;font-size:.82rem;
          color:var(--ink-3);margin:0 0 10px;overflow-wrap:anywhere}
        pre.out{background:var(--sunk);border:1px solid var(--line);padding:14px;margin:0;
          max-height:460px;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;
          font-family:ui-monospace,"Cascadia Mono",Consolas,monospace;font-size:.82rem;line-height:1.55}
        .state{font-size:.85rem;margin:0 0 10px}
        .state.ok{color:var(--ok)} .state.ko{color:var(--crit)} .state.busy{color:var(--ink-3)}
        ul.pages{list-style:none;padding:0;margin:0;display:flex;flex-wrap:wrap;gap:8px}
        ul.pages a{display:inline-block;padding:6px 11px;border:1px solid var(--line);
          border-radius:3px;color:var(--p3);text-decoration:none;font-size:.88rem}
        ul.pages a:hover{border-color:var(--p3)}
        footer{margin-top:34px;padding-top:18px;border-top:1px solid var(--line);
          color:var(--ink-3);font-size:.84rem}
        </style>
        </head>
        <body>
        <main class="page">
        <header>
          <h1>CoachingIA</h1>
          <p class="lede">Console locale. Les commandes tournent sur cette machine, sur vos
          transcripts, et rien ne sort de votre poste.</p>
        </header>

        <section class="card">
          <h2>Réglages</h2>
          <div class="grid">
            <div><label for="root">Dossier des transcripts</label>
              <input id="root" value="__ROOT__" placeholder="laisser vide = dossier par défaut"></div>
            <div><label for="out">Dossier de sortie</label><input id="out" value="__OUT__"></div>
            <div><label for="lens">Lentille</label><select id="lens">__LENSES__</select></div>
            <div><label for="race">Camp</label><select id="race">__RACES__</select></div>
            <div><label for="week">Semaine (bilan)</label>
              <input id="week" placeholder="2026-W34 — vide = dernière close"></div>
            <div><label for="mois">Mois en arrière (rétro)</label>
              <input id="mois" type="number" min="1" max="60" placeholder="vide = tout l'historique"></div>
          </div>
          <p class="hint">Le dossier des lentilles est <code>__LENSDIR__</code>.</p>
        </section>

        <section class="card">
          <h2>Le rituel</h2>
          <div class="row">
            <button class="primary" data-cmd="bilan">Bilan de la semaine</button>
            <button class="primary" data-cmd="retro">Rétrospective</button>
            <button data-cmd="defi">Défi</button>
            <button data-cmd="moment">Moment opportun</button>
            <button data-cmd="lens">Voir la lentille</button>
          </div>
          <h2 style="margin-top:22px">Regarder les traces</h2>
          <div class="row">
            <button data-cmd="probe">Sonde de format</button>
            <button data-cmd="analyze">Sessions et signaux</button>
            <button data-cmd="segment">Découpe en tâches</button>
            <button data-cmd="usage">Jetons et modèles</button>
            <button data-cmd="team">Vue d'équipe</button>
          </div>
        </section>

        <section class="card">
          <h2>Pages produites</h2>
          <ul class="pages" id="pages"><li class="hint">Aucune pour l'instant.</li></ul>
        </section>

        <section class="card">
          <h2>Sortie</h2>
          <p class="state" id="state">Prêt.</p>
          <p class="cmdline" id="cmdline"></p>
          <pre class="out" id="out">Choisissez une commande ci-dessus.</pre>
        </section>

        <footer>Serveur local, écoute sur 127.0.0.1 uniquement. Fermez la fenêtre du terminal pour l'arrêter.</footer>
        </main>

        <script>
        const T = "__TOKEN__";
        const $ = id => document.getElementById(id);

        function options(cmd){
          const o = {};
          const put = (k, v) => { if (v && v.trim()) o[k] = v.trim(); };
          put("--root", $("root").value);
          put("--out",  $("out").value);
          put("--lens", $("lens").value);
          put("--race", $("race").value);
          if (cmd === "bilan") put("--week", $("week").value);
          if (cmd === "retro") put("--mois", $("mois").value);
          return o;
        }

        function pages(list){
          const ul = $("pages");
          if (!list || !list.length){ ul.innerHTML = '<li class="hint">Aucune pour l\'instant.</li>'; return; }
          ul.innerHTML = "";
          for (const name of list){
            const li = document.createElement("li");
            const a = document.createElement("a");
            a.href = "/fichier?t=" + T + "&p=" + encodeURIComponent(name);
            a.target = "_blank"; a.rel = "noopener"; a.textContent = name;
            li.appendChild(a); ul.appendChild(li);
          }
        }

        async function run(cmd, button){
          const buttons = document.querySelectorAll("button[data-cmd]");
          buttons.forEach(b => b.disabled = true);
          $("state").className = "state busy";
          $("state").textContent = "En cours — " + cmd + "…";
          $("out").textContent = "";
          $("cmdline").textContent = "";
          try {
            const res = await fetch("/run?t=" + T, {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ command: cmd, options: options(cmd) })
            });
            if (!res.ok){ throw new Error(await res.text()); }
            const data = await res.json();
            $("cmdline").textContent = "coachingia " + data.line;
            $("out").textContent = data.output;
            $("state").className = "state " + (data.code === 0 ? "ok" : "ko");
            $("state").textContent = (data.code === 0 ? "Terminé" : "Terminé en erreur (code " + data.code + ")")
              + " en " + (data.ms / 1000).toFixed(1) + " s.";
            pages(data.pages);
          } catch (e) {
            $("state").className = "state ko";
            $("state").textContent = "Échec : " + e.message;
          } finally {
            buttons.forEach(b => b.disabled = false);
          }
        }

        document.querySelectorAll("button[data-cmd]").forEach(b =>
          b.addEventListener("click", () => run(b.dataset.cmd, b)));

        // Le camp ne concerne que la lentille qui le propose : on masque le reste.
        function syncRaces(){
          const lens = $("lens").value;
          for (const opt of $("race").options){
            if (!opt.dataset.lens) continue;
            const ok = opt.dataset.lens === lens;
            opt.hidden = !ok;
            if (!ok && opt.selected) $("race").value = "";
          }
        }
        $("lens").addEventListener("change", syncRaces);
        syncRaces();

        fetch("/pages?t=" + T).then(r => r.json()).then(pages).catch(() => {});
        </script>
        </body>
        </html>
        """;
}
