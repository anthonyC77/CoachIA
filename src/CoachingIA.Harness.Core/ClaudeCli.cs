using System.Diagnostics;

namespace CoachingIA.Harness.Core;

/// <summary>
/// Un appel non interactif à Claude, vu par ce qui s'en sert.
///
/// L'interface existe pour une seule raison : la critique de prompt, l'écriture
/// de scènes et le juge d'évaluation doivent pouvoir être mis à l'épreuve hors
/// ligne, sans qu'aucune vérification ne lance de processus. Le vrai appel est
/// éprouvé à part, contre une sonde locale.
/// </summary>
public interface IClaudeCli
{
    /// <summary>Le binaire est-il là ? On regarde avant de lancer, jamais après avoir échoué.</summary>
    bool EstDisponible { get; }

    /// <summary>Dernière erreur rencontrée, pour l'expliquer plutôt que la taire.</summary>
    string? DerniereErreur { get; }

    /// <summary>La sortie standard brute, ou <c>null</c> si l'appel n'a pas abouti.</summary>
    string? Demander(string instruction, string? schema);
}

/// <summary>
/// L'appel à <c>claude -p</c>, en un seul endroit.
///
/// <para>Il utilise la session déjà authentifiée du poste — jamais
/// <c>--bare</c>, qui ignore les identifiants d'abonnement et exigerait une clé
/// d'API facturée à part.</para>
///
/// <para>Trois précautions, parce qu'un outil de coaching ne doit jamais
/// devenir un point de panne : on vérifie que le binaire existe, on borne le
/// temps, et toute erreur rend la main à l'appelant, qui a toujours un repli
/// hors ligne.</para>
///
/// <para>Les deux tubes se lisent <strong>en même temps</strong>, avant
/// d'attendre la fin. Un enfant bavard sur la sortie d'erreur remplit son tube,
/// se bloque en écriture, et n'atteint jamais la fin que l'on attendait : le
/// délai finissait par trancher, et l'appel coûtait sa durée entière pour
/// rien.</para>
/// </summary>
public sealed class ClaudeCli(TimeSpan? delai = null, string executable = "claude") : IClaudeCli
{
    private readonly TimeSpan _delai = delai ?? TimeSpan.FromSeconds(90);

    public string? DerniereErreur { get; private set; }

    public bool EstDisponible => Existe(executable);

    public string? Demander(string instruction, string? schema)
    {
        DerniereErreur = null;
        if (!EstDisponible) { DerniereErreur = $"« {executable} » introuvable dans le PATH"; return null; }

        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(instruction);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("json");
        if (schema is not null) { psi.ArgumentList.Add("--json-schema"); psi.ArgumentList.Add(schema); }

        try
        {
            using var processus = Process.Start(psi);
            if (processus is null) { DerniereErreur = "le processus n'a pas démarré"; return null; }
            processus.StandardInput.Close();

            // Les deux tubes se vident en parallèle, avant d'attendre la fin :
            // c'est toute la correction. Lire la sortie d'erreur seulement
            // après coup laisse l'enfant bloqué en écriture dès qu'il dépasse
            // la taille d'un tube, et l'appel meurt sur son délai.
            var sortie = processus.StandardOutput.ReadToEndAsync();
            var erreur = processus.StandardError.ReadToEndAsync();

            if (!processus.WaitForExit((int)_delai.TotalMilliseconds))
            {
                try { processus.Kill(entireProcessTree: true); } catch { /* au mieux */ }
                DerniereErreur = $"délai dépassé ({Duree()})";
                return null;
            }

            if (processus.ExitCode != 0)
            {
                DerniereErreur = $"code de sortie {processus.ExitCode} : {erreur.GetAwaiter().GetResult().Trim()}";
                return null;
            }

            return sortie.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DerniereErreur = ex.Message;
            return null;
        }
    }

    private string Duree()
        => _delai.TotalSeconds < 120 ? $"{_delai.TotalSeconds:F0} s" : $"{_delai.TotalMinutes:F0} min";

    private static bool Existe(string nom)
    {
        if (Path.IsPathRooted(nom)) return File.Exists(nom);
        var chemins = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];
        return chemins.Any(dossier => extensions.Any(ext =>
            !string.IsNullOrWhiteSpace(dossier) && File.Exists(Path.Combine(dossier, nom + ext))));
    }
}
