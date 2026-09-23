// Un outil est un type, pas une description.
// Ce fichier est la SEULE source de vérité de l'outil `rechercher_documents`.
// Le schéma JSON (eval/schemas/tools/rechercher_documents.schema.json) est généré
// depuis ce type — jamais écrit à la main — et sert trois fois : description pour
// le modèle, validation E/S dans le harnais, assertion dans l'eval.
namespace Nexus.Tools.Contracts;

[Tool("rechercher_documents", SideEffect = false,
      Description = "Recherche hybride dans le référentiel ; renvoie les k meilleurs passages avec leur document source.")]
public sealed record RechercherDocumentsInput(
    [Doc("Question telle que posée par l'utilisateur")] string Query,
    [Doc("Nombre de passages à renvoyer")] int K = 5);

[ToolOutput("rechercher_documents")]
public sealed record RechercherDocumentsOutput(
    [Doc("Passages classés par pertinence décroissante")] IReadOnlyList<Passage> Results);

public sealed record Passage(
    [Doc("Identifiant du document tel que l'index le renvoie")] string DocId,
    string ChunkId,
    double Score,
    string Text);
