using System.Text.Json;
using Sage100Api.Contracts;
using Sage100Api.Journal;
using Sage100Api.Lectures;
using Sage100Api.Worker;

namespace Sage100Api.Ecritures;

/// <summary>Résultat d'une écriture, traduit ensuite en réponse HTTP.</summary>
public sealed record ResultatEcriture<T>(T? Valeur, string? CodeErreur, string? Message, bool DejaTraitee = false)
{
    public static ResultatEcriture<T> Reussite(T v, bool deja = false) => new(v, null, null, deja);
    public static ResultatEcriture<T> Echec(string code, string message) => new(default, code, message);
}

public static class CodesApi
{
    public const string EnCours = "EN_COURS";
    public const string CommandeInconnue = "COMMANDE_INCONNUE";
}

/// <summary>Enchaîne : journal (anti-doublon) -> worker Objets Métiers -> journal.</summary>
public sealed class ServiceEcritures(JournalOperations journal, IWorkerClient worker, ILecturesSage lectures)
{
    public const string TypeCommande = "commande";
    public const string TypeEncaissement = "encaissement";

    public async Task<ResultatEcriture<CommandeResult>> CreerCommande(CommandeRequest c, string application, CancellationToken ct)
    {
        var cle = JournalOperations.Cle(TypeCommande, c.IdExterne);
        var existante = journal.Reserver(cle, TypeCommande, application);
        if (existante != null) return DejaVue<CommandeResult>(existante, r => r.DejaExistante = true);

        var reponse = await worker.Envoyer(Operations.CreerCommande, c, ct);
        return Conclure<CommandeResult>(cle, reponse, r => r.Piece);
    }

    public async Task<ResultatEcriture<EncaissementResult>> CreerEncaissement(string idCommande, EncaissementRequest e, string application, CancellationToken ct)
    {
        var piece = await PieceCommande(idCommande);
        if (piece == null)
            return ResultatEcriture<EncaissementResult>.Echec(CodesApi.CommandeInconnue,
                $"Aucune commande Sage pour l'identifiant {idCommande}. Envoyez d'abord la commande.");

        var cle = JournalOperations.Cle(TypeEncaissement, e.IdExterne);
        var existante = journal.Reserver(cle, TypeEncaissement, application);
        if (existante != null) return DejaVue<EncaissementResult>(existante, r => r.DejaExistant = true);

        var reponse = await worker.Envoyer(Operations.CreerEncaissement, new EncaissementCommandeRequest { PieceCommande = piece, Encaissement = e }, ct);
        return Conclure<EncaissementResult>(cle, reponse, r => r.PieceCommande);
    }

    /// <summary>Pièce Sage d'une commande : d'abord le journal, sinon la base Sage (DO_RefExterne).</summary>
    public async Task<string?> PieceCommande(string idExterne) =>
        journal.Lire(JournalOperations.Cle(TypeCommande, idExterne)) is { Statut: StatutOperation.Ok, Piece: { } p }
            ? p
            : await lectures.PieceCommande(idExterne);

    ResultatEcriture<T> DejaVue<T>(Operation op, Action<T> marquer)
    {
        if (op.Statut == StatutOperation.EnCours)
            return ResultatEcriture<T>.Echec(CodesApi.EnCours, "Cette opération est déjà en cours de traitement. Réessayez dans quelques secondes.");
        var valeur = JsonSerializer.Deserialize<T>(op.Resultat!, WorkerProtocol.Json)!;
        marquer(valeur);
        return ResultatEcriture<T>.Reussite(valeur, deja: true);
    }

    ResultatEcriture<T> Conclure<T>(string cle, WorkerResponse reponse, Func<T, string> piece)
    {
        if (!reponse.Ok || reponse.Resultat is null)
        {
            // L'opération pourra être retentée : le worker a sa propre barrière anti-doublon côté Sage.
            journal.Terminer(cle, StatutOperation.Erreur, null, reponse.MessageErreur);
            return ResultatEcriture<T>.Echec(reponse.CodeErreur ?? CodesErreur.Technique, reponse.MessageErreur ?? "Erreur inconnue.");
        }
        var valeur = reponse.Resultat.Value.Deserialize<T>(WorkerProtocol.Json)!;
        journal.Terminer(cle, StatutOperation.Ok, piece(valeur), reponse.Resultat.Value.GetRawText());
        return ResultatEcriture<T>.Reussite(valeur);
    }
}
