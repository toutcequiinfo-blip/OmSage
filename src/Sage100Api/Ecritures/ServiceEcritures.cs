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
public sealed class ServiceEcritures(JournalOperations journal, IWorkerClient worker, ILecturesSage lectures, ControleStock stock)
{
    public const string TypeCommande = "commande";
    public const string TypeEncaissement = "encaissement";

    public async Task<ResultatEcriture<CommandeResult>> CreerCommande(CommandeRequest c, string application, Utilisateur? utilisateur, CancellationToken ct)
    {
        var cle = JournalOperations.Cle(TypeCommande, c.IdExterne);
        var existante = journal.Reserver(cle, TypeCommande, Origine(application, utilisateur));
        if (existante != null) return DejaVue<CommandeResult>(existante, r => r.DejaExistante = true);

        // Une commande déjà dans Sage (renvoi après perte du journal) a déjà réservé son stock : pas de contrôle,
        // le worker la retrouvera par DO_RefExterne.
        if (await lectures.PieceCommande(c.IdExterne) is null && await stock.Verifier(c) is { } manque)
        {
            journal.Terminer(cle, StatutOperation.Erreur, null, manque);
            return ResultatEcriture<CommandeResult>.Echec(CodesErreur.SageMetier, manque);
        }

        var reponse = await worker.Envoyer(Operations.CreerCommande, new CommandeWorkerRequest { Commande = c, Auteur = utilisateur?.Auteur() }, ct);
        return Conclure<CommandeResult>(cle, reponse, r => r.Piece);
    }

    public async Task<ResultatEcriture<EncaissementResult>> CreerEncaissement(string idCommande, EncaissementRequest e, string application, Utilisateur? utilisateur, CancellationToken ct)
    {
        var piece = await PieceCommande(idCommande);
        if (piece == null)
            return ResultatEcriture<EncaissementResult>.Echec(CodesApi.CommandeInconnue,
                $"Aucune commande Sage pour l'identifiant {idCommande}. Envoyez d'abord la commande.");

        var cle = JournalOperations.Cle(TypeEncaissement, e.IdExterne);
        var existante = journal.Reserver(cle, TypeEncaissement, Origine(application, utilisateur));
        if (existante != null) return DejaVue<EncaissementResult>(existante, r => r.DejaExistant = true);

        var reponse = await worker.Envoyer(Operations.CreerEncaissement,
            new EncaissementCommandeRequest { PieceCommande = piece, Encaissement = e, Auteur = utilisateur?.Auteur() }, ct);
        return Conclure<EncaissementResult>(cle, reponse, r => r.PieceCommande);
    }

    /// <summary>Pièce Sage d'une commande : d'abord le journal, sinon la base Sage (DO_RefExterne).</summary>
    public async Task<string?> PieceCommande(string idExterne) =>
        journal.Lire(JournalOperations.Cle(TypeCommande, idExterne)) is { Statut: StatutOperation.Ok, Piece: { } p }
            ? p
            : await lectures.PieceCommande(idExterne);

    /// <summary>Colonne « application » du journal : application cliente et utilisateur Sage connecté.</summary>
    static string Origine(string application, Utilisateur? u) => u == null ? application : $"{application} / {u.Login}";

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
