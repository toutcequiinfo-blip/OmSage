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
public sealed class ServiceEcritures(JournalOperations journal, IWorkerClient worker, ILecturesSage lectures, ILecturesTarifs tarifs, ControleStock stock)
{
    public const string TypeCommande = "commande";
    public const string TypeEncaissement = "encaissement";

    public async Task<ResultatEcriture<CommandeResult>> CreerCommande(CommandeRequest c, string application, Utilisateur? utilisateur, CancellationToken ct)
    {
        var cle = JournalOperations.Cle(TypeCommande, c.IdExterne);
        var existante = journal.Reserver(cle, TypeCommande, Origine(application, utilisateur));
        if (existante != null) return DejaVue<CommandeResult>(existante, r => r.DejaExistante = true);
        try { return await CreerCommandeReservee(cle, c, utilisateur, ct); }
        catch (Exception e) { Liberer(cle, e); throw; }
    }

    async Task<ResultatEcriture<CommandeResult>> CreerCommandeReservee(string cle, CommandeRequest c, Utilisateur? utilisateur, CancellationToken ct)
    {
        // Une commande déjà dans Sage (renvoi après perte du journal) a déjà réservé son stock : pas de contrôle,
        // le worker la retrouvera par DO_RefExterne.
        if (await lectures.PieceCommande(c.IdExterne) is null && await stock.Verifier(c) is { } manque)
        {
            journal.Terminer(cle, StatutOperation.Erreur, null, manque);
            return ResultatEcriture<CommandeResult>.Echec(CodesErreur.SageMetier, manque);
        }

        c.TypeDocument = TypesPiece.Normaliser(c.TypeDocument);
        await AppliquerTarifs(c);
        var reponse = await worker.Envoyer(Operations.CreerCommande, new CommandeWorkerRequest { Commande = c, Auteur = utilisateur?.Auteur() }, ct);
        return Conclure<CommandeResult>(cle, reponse, r => r.Piece);
    }

    /// <summary>
    /// Prix de chaque ligne selon le tarif du client (sa fiche, sinon sa catégorie tarifaire), avec gamme, conditionnement
    /// et remises. Le worker les impose sur la pièce si Sage en a calculé d'autres. Les prix envoyés par l'application sont ignorés.
    /// </summary>
    public async Task AppliquerTarifs(CommandeRequest c)
    {
        foreach (var l in c.Lignes) { l.PrixUnitaire = null; l.PrixTTC = false; l.Remises = null; }
        var client = await lectures.Client(c.Client.Trim());
        if (client is null) return; // client inconnu : le worker renverra 404
        var donnees = await tarifs.Tarifs(client.Numero, client.CategorieTarif, c.Lignes.Select(l => l.Article).ToList());
        foreach (var l in c.Lignes)
        {
            var article = await lectures.Article(l.Article.Trim());
            if (article is null) continue;
            Conditionnement? cond = null;
            if (!string.IsNullOrEmpty(l.Conditionnement))
            {
                var qte = (decimal)(l.QuantiteConditionnement ?? 0);
                cond = donnees.Conditionnements.FirstOrDefault(x => string.Equals(x.Article, article.Reference, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(x.Enumere.Trim(), l.Conditionnement!.Trim(), StringComparison.OrdinalIgnoreCase) && x.Quantite == qte)
                       // Conditionnement inconnu : le worker le refusera ; le prix reste celui de l'unité multiplié.
                       ?? new Conditionnement(article.Reference, 0, l.Conditionnement!, qte, null, null, false);
            }
            var p = Tarification.Calculer(donnees, article.Reference, article.PrixVenteHT, article.PrixTTC, client.Numero, client.CategorieTarif,
                l.Gamme1, l.Gamme2, cond, (decimal)l.Quantite);
            l.PrixUnitaire = (double)p.PrixUnitaire;
            l.PrixTTC = p.PrixTTC;
            l.Remises = p.Remises.Select(r => new RemiseLigne { Type = r.Type, Valeur = (double)r.Valeur }).ToList();
        }
    }

    /// <summary>
    /// Supprime de Sage la pièce créée pour cette commande, tant qu'elle n'a aucun encaissement (le client a changé d'avis
    /// sur l'écran d'encaissement). Le journal passe en erreur : la même commande pourrait être renvoyée et recréée.
    /// </summary>
    public async Task<ResultatEcriture<SuppressionResult>> SupprimerCommande(string idExterne, Utilisateur? utilisateur, CancellationToken ct)
    {
        var cle = JournalOperations.Cle(TypeCommande, idExterne);
        if (journal.Lire(cle) is { Statut: StatutOperation.EnCours } op && DateTime.UtcNow - op.MajLe.ToUniversalTime() < JournalOperations.DelaiAbandon)
            return ResultatEcriture<SuppressionResult>.Echec(CodesApi.EnCours, "La pièce est encore en cours de création. Réessayez dans quelques secondes.");
        var reponse = await worker.Envoyer(Operations.SupprimerCommande, new SuppressionWorkerRequest { IdExterne = idExterne, Auteur = utilisateur?.Auteur() }, ct);
        if (!reponse.Ok || reponse.Resultat is null)
            return ResultatEcriture<SuppressionResult>.Echec(reponse.CodeErreur ?? CodesErreur.Technique, reponse.MessageErreur ?? "Erreur inconnue.");
        var r = reponse.Resultat.Value.Deserialize<SuppressionResult>(WorkerProtocol.Json)!;
        if (journal.Lire(cle) != null)
            journal.Terminer(cle, StatutOperation.Erreur, null, r.DejaAbsente ? "Aucune pièce dans Sage." : $"{r.Piece} supprimée depuis la borne.");
        return ResultatEcriture<SuppressionResult>.Reussite(r);
    }

    public async Task<ResultatEcriture<EncaissementResult>> CreerEncaissement(string idCommande, EncaissementRequest e, string application, Utilisateur? utilisateur, CancellationToken ct)
    {
        var piece = await PieceCommande(idCommande);
        if (piece == null)
            return ResultatEcriture<EncaissementResult>.Echec(CodesApi.CommandeInconnue,
                $"Aucune commande Sage pour l'identifiant {idCommande}. Envoyez d'abord la commande.");
        return await Encaisser(piece, e, application, utilisateur, ct);
    }

    /// <summary>
    /// Encaissement sur un bon de commande désigné par son numéro de pièce Sage : commande saisie dans Sage,
    /// ou prise par un vendeur sur la borne. Le worker vérifie que la pièce existe.
    /// </summary>
    public Task<ResultatEcriture<EncaissementResult>> CreerEncaissementSurPiece(string piece, EncaissementRequest e, string application, Utilisateur? utilisateur,
        CancellationToken ct) =>
        Encaisser(piece.Trim().ToUpperInvariant(), e, application, utilisateur, ct);

    async Task<ResultatEcriture<EncaissementResult>> Encaisser(string piece, EncaissementRequest e, string application, Utilisateur? utilisateur, CancellationToken ct)
    {
        var cle = JournalOperations.Cle(TypeEncaissement, e.IdExterne);
        var existante = journal.Reserver(cle, TypeEncaissement, Origine(application, utilisateur));
        if (existante != null) return DejaVue<EncaissementResult>(existante, r => r.DejaExistant = true);

        try
        {
            var reponse = await worker.Envoyer(Operations.CreerEncaissement,
                new EncaissementCommandeRequest { PieceCommande = piece, Encaissement = e, Auteur = utilisateur?.Auteur() }, ct);
            return Conclure<EncaissementResult>(cle, reponse, r => r.PieceCommande);
        }
        catch (Exception ex) { Liberer(cle, ex); throw; }
    }

    /// <summary>
    /// Erreur imprévue après la réservation (lecture SQL, tarifs...) : l'opération passe en erreur pour pouvoir être renvoyée,
    /// au lieu de rester « en cours » et de refuser tous les renvois.
    /// </summary>
    void Liberer(string cle, Exception e) => journal.Terminer(cle, StatutOperation.Erreur, null, e.Message);

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
