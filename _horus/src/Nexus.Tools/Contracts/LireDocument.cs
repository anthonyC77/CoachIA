namespace Nexus.Tools.Contracts;

[Tool("lire_document", SideEffect = false,
      Description = "Renvoie le texte intégral d'un document du référentiel à partir de son identifiant.")]
public sealed record LireDocumentInput(
    [Doc("Identifiant du document (DOC-…)")] string DocId);

[ToolOutput("lire_document")]
public sealed record LireDocumentOutput(
    string DocId,
    string Title,
    string Text,
    [Doc("Version du document, pour la provenance")] string Version);
