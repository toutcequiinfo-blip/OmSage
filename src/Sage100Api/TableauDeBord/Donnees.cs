using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>Section « TableauDeBord » de la configuration.</summary>
public sealed class TableauDeBordOptions
{
    /// <summary>
    /// Connexion SQL propre au tableau de bord, idéalement un compte qui n'a que le droit de lecture
    /// (deploy\compte-lecture-seule.ps1). Vide : la chaîne SQL de la société. Avec plusieurs sociétés, la base de la chaîne est
    /// remplacée par celle de la société lue (le compte doit avoir le droit de lecture sur chacune).
    /// </summary>
    public string ChaineSql { get; set; } = "";
    /// <summary>
    /// Heures d'actualisation automatique (HH:mm, séparées par des virgules), en plus de celle du démarrage.
    /// Un texte et non une liste : une liste de la configuration s'ajouterait aux heures par défaut au lieu de les remplacer.
    /// </summary>
    public string Actualisations { get; set; } = "07:00,12:00,17:00";
    /// <summary>Nombre d'exercices chargés : l'exercice en cours et les précédents (4 = N à N-3).</summary>
    public int Exercices { get; set; } = 4;
    /// <summary>Durée maximale d'une requête SQL d'actualisation.</summary>
    public int DelaiSqlSecondes { get; set; } = 600;
    /// <summary>
    /// Profil par login Sage : Direction, Comptable, Commercial, Vendeur. Sans ligne ici : administrateur Sage = Direction,
    /// collaborateur vendeur = Vendeur (ses clients seulement), sinon <see cref="ProfilParDefaut"/>.
    /// </summary>
    public Dictionary<string, string> Profils { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Profil des autres utilisateurs Sage : « Aucun » (pas d'accès) par défaut.</summary>
    public string ProfilParDefaut { get; set; } = "Aucun";
    /// <summary>Seuils des alertes (montants dans la devise du dossier).</summary>
    public SeuilsAlertes Seuils { get; set; } = new();
}

public sealed class SeuilsAlertes
{
    /// <summary>Trésorerie en dessous de ce montant : alerte.</summary>
    public decimal Tresorerie { get; set; } = 0;
    /// <summary>Baisse du CA du mois par rapport au même mois N-1, en % : alerte au-delà.</summary>
    public decimal BaisseCaPourcent { get; set; } = 10;
    /// <summary>Taux de marge (marge / coût de revient) en dessous de ce % : alerte.</summary>
    public decimal TauxMargeMini { get; set; } = 15;
    /// <summary>Client facturé dans les 12 derniers mois mais sans facture depuis ce nombre de jours : client à relancer.</summary>
    public int JoursSansCommande { get; set; } = 60;
    /// <summary>Article sans sortie depuis ce nombre de jours : dormant.</summary>
    public int JoursDormant { get; set; } = 180;
}

public sealed record Exercice(int Numero, DateTime Debut, DateTime Fin);

/// <summary>Écritures générales regroupées par mois (période JM_Date), compte, journal et tiers.</summary>
public sealed class FaitCompta
{
    public DateTime Mois { get; init; }
    /// <summary>Jour des écritures (les filtres Du / Au sont au jour près) ; à défaut, le mois.</summary>
    public DateTime Date { get => _date == default ? Mois : _date; init => _date = value; }
    DateTime _date;
    public string Compte { get; init; } = "";
    public string Journal { get; init; } = "";
    public string? Tiers { get; init; }
    /// <summary>Écritures d'à-nouveaux (EC_ANType ≠ 0) : reprises des soldes en début d'exercice.</summary>
    public bool ANouveau { get; init; }
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public int Nombre { get; init; }
}

/// <summary>Ventilation analytique (F_ECRITUREA) par jour, plan, section, compte et journal. Montant positif = débit.</summary>
public sealed class FaitAnalytique
{
    public DateTime Mois { get; init; }
    /// <summary>Jour des écritures (les filtres Du / Au sont au jour près) ; à défaut, le mois.</summary>
    public DateTime Date { get => _date == default ? Mois : _date; init => _date = value; }
    DateTime _date;
    public int Plan { get; init; }
    public string Section { get; init; } = "";
    public string Compte { get; init; } = "";
    public string Journal { get; init; } = "";
    public decimal Montant { get; init; }
}

/// <summary>
/// Lignes de factures (ventes : DO_Type 6, 7 ; achats : 16, 17), et de bons de livraison, de retour et d'avoir financier
/// (3, 4, 5 ; 13, 14, 15) que le tableau compte ou non selon <see cref="DocumentsCa"/>, regroupées par jour, type, article, tiers, commercial et dépôt.
/// Les factures de retour et d'avoir (DO_Provenance 1, 2), bons de retour et d'avoir financier sont en négatif.
/// </summary>
public sealed class FaitLigne
{
    /// <summary>0 = vente, 1 = achat.</summary>
    public int Domaine { get; init; }
    /// <summary>Type de pièce (DO_Type) : facture par défaut ; bon de livraison, de retour, d'avoir financier selon <see cref="DocumentsCa"/>.</summary>
    public int Type { get; init; } = 6;
    /// <summary>Provenance d'une facture (DO_Provenance) : 1 = facture de retour, 2 = facture d'avoir.</summary>
    public int Provenance { get; init; }
    public DateTime Mois { get; init; }
    /// <summary>Jour des factures (les filtres Du / Au sont au jour près) ; à défaut, le mois.</summary>
    public DateTime Date { get => _date == default ? Mois : _date; init => _date = value; }
    DateTime _date;
    public string Article { get; init; } = "";
    public string Tiers { get; init; } = "";
    public int? Commercial { get; init; }
    public int? Depot { get; init; }
    public decimal MontantHT { get; init; }
    public decimal Quantite { get; init; }
    /// <summary>Coût de revient de la ligne : DL_PrixRU × DL_Qte (prix de revient mémorisé par Sage sur la ligne).</summary>
    public decimal Cout { get; init; }
}

/// <summary>Une facture (vente ou achat) : sert au CA du jour, au nombre de factures et au panier moyen.</summary>
public sealed class FaitPiece
{
    public int Domaine { get; init; }
    /// <summary>Type de pièce (DO_Type) : facture par défaut.</summary>
    public int Type { get; init; } = 6;
    /// <summary>Provenance d'une facture (DO_Provenance) : 1 = facture de retour, 2 = facture d'avoir.</summary>
    public int Provenance { get; init; }
    public bool Facture => Type % 10 is 6 or 7;
    public DateTime Date { get; init; }
    public string Piece { get; init; } = "";
    public string Tiers { get; init; } = "";
    public int? Commercial { get; init; }
    public int? Depot { get; init; }
    public decimal MontantHT { get; init; }
    /// <summary>Facture de retour ou d'avoir, bon de retour ou d'avoir financier.</summary>
    public bool Avoir { get; init; }
}

/// <summary>Documents de vente en cours : devis, commandes, préparations et bons de livraison non clôturés.</summary>
public sealed class FaitEnCours
{
    public int Type { get; init; }
    public DateTime Date { get; init; }
    public DateTime? DateLivraison { get; init; }
    public string Piece { get; init; } = "";
    public string Tiers { get; init; } = "";
    public int? Commercial { get; init; }
    public decimal MontantHT { get; init; }
}

/// <summary>
/// Lignes de vente issues d'un bon de commande, pour le taux de transformation commande → livraison :
/// le reliquat des bons de commande (DO_Type 1) et les lignes déjà transformées (préparation, bon de livraison, facture)
/// qui gardent le numéro du bon d'origine (DL_PieceBC).
/// </summary>
public sealed class FaitCommande
{
    /// <summary>Numéro du bon de commande d'origine.</summary>
    public string Commande { get; init; } = "";
    public DateTime DateCommande { get; init; }
    /// <summary>Pièce où se trouve la ligne aujourd'hui (le bon de commande lui-même, une préparation, un BL ou une facture).</summary>
    public string Piece { get; init; } = "";
    public int Type { get; init; }
    /// <summary>Bon de commande clôturé : son reliquat ne sera pas livré.</summary>
    public bool Cloture { get; init; }
    /// <summary>Date de livraison (date du BL) des lignes livrées.</summary>
    public DateTime? DateLivraison { get; init; }
    public string Article { get; init; } = "";
    public string Tiers { get; init; } = "";
    public int? Commercial { get; init; }
    public int? Depot { get; init; }
    public decimal Quantite { get; init; }
    public decimal MontantHT { get; init; }

    public bool Livree => Type is 3 or 6 or 7;
    public bool Preparee => Type == 2;
    public bool EnAttente => Type == 1 && !Cloture;
    public bool NonServie => Type == 1 && Cloture;
}

public sealed class LigneStock
{
    public string Article { get; init; } = "";
    public int Depot { get; init; }
    public decimal Quantite { get; init; }
    public decimal Valeur { get; init; }
    public decimal Mini { get; init; }
    public decimal Maxi { get; init; }
    public decimal Reserve { get; init; }
    public decimal Commande { get; init; }
}

/// <summary>Écriture de tiers non lettrée (client ou fournisseur). Montant positif = dû (par le client, ou au fournisseur).</summary>
public sealed class EcheanceTiers
{
    /// <summary>0 = client, 1 = fournisseur.</summary>
    public int Type { get; init; }
    public string Tiers { get; init; } = "";
    public string? Journal { get; init; }
    public DateTime Date { get; init; }
    public string? Piece { get; init; }
    public string? Libelle { get; init; }
    public DateTime? DateEcheance { get; init; }
    public decimal Montant { get; init; }
    public DateTime? DateRelance { get; init; }
}

/// <summary>Règlements de la gestion commerciale (F_CREGLEMENT) par mois, type de tiers et mode.</summary>
public sealed class FaitReglement
{
    public DateTime Mois { get; init; }
    /// <summary>0 = client, 1 = fournisseur.</summary>
    public int Type { get; init; }
    public int Mode { get; init; }
    public string? Tiers { get; init; }
    public decimal Montant { get; init; }
    public int Nombre { get; init; }
}

public sealed class RefCompte { public string Numero { get; init; } = ""; public string? Intitule { get; init; } }
public sealed class RefJournal { public string Code { get; init; } = ""; public string? Intitule { get; init; } public int Type { get; init; } }
public sealed class RefTiers
{
    public string Numero { get; init; } = "";
    public string? Intitule { get; init; }
    public int Type { get; init; }
    /// <summary>Représentant (CO_No de la fiche).</summary>
    public int? Representant { get; init; }
    public int? Categorie { get; init; }
    /// <summary>Qualité de la fiche (CT_Qualite, texte libre : Grossiste, Détaillant...).</summary>
    public string? Qualite { get; init; }
}
public sealed class RefSection { public int Plan { get; init; } public string Numero { get; init; } = ""; public string? Intitule { get; init; } }
public sealed class RefArticle { public string Reference { get; init; } = ""; public string? Designation { get; init; } public string? Famille { get; init; } }
public sealed class RefCode { public int Numero { get; init; } public string? Intitule { get; init; } }
public sealed class RefFamille { public string Code { get; init; } = ""; public string? Intitule { get; init; } }

/// <summary>Écriture affichée au zoom d'une cellule du tableau comptable.</summary>
public sealed class DetailEcriture
{
    public DateTime Date { get; init; }
    public string Journal { get; init; } = "";
    public string? Piece { get; init; }
    public string Compte { get; init; } = "";
    public string? Tiers { get; init; }
    public string? Libelle { get; init; }
    public DateTime? Echeance { get; init; }
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public string? Lettrage { get; init; }
}

/// <summary>Ligne de facture affichée au zoom d'une cellule du tableau commercial.</summary>
public sealed class DetailLigne
{
    public DateTime Date { get; init; }
    public string Piece { get; init; } = "";
    public string Tiers { get; init; } = "";
    public string Article { get; init; } = "";
    public string? Designation { get; init; }
    public decimal Quantite { get; init; }
    public decimal MontantHT { get; init; }
    public decimal Cout { get; init; }
}

public sealed record FiltreDetailCompta(DateTime Du, DateTime Au, IReadOnlyList<string>? Comptes, IReadOnlyList<string>? Journaux, string? Tiers, bool ANouveaux, int Taille)
{
    /// <summary>Exclusions des filtres à choix multiple (NOT IN), et tiers gardés quand plusieurs sont cochés.</summary>
    public IReadOnlyList<string>? ComptesExclus { get; init; }
    public IReadOnlyList<string>? JournauxExclus { get; init; }
    public IReadOnlyList<string>? TiersListe { get; init; }
    public IReadOnlyList<string>? TiersExclus { get; init; }
}

public sealed record FiltreDetailVentes(int Domaine, DateTime Du, DateTime Au, string? Article, IReadOnlyList<string>? Articles, string? Tiers,
    IReadOnlyList<string>? TiersListe, int? Commercial, int? Depot, int Taille, DocumentsCa? Documents = null)
{
    /// <summary>Exclusions des filtres à choix multiple (NOT IN), et commerciaux et dépôts gardés quand plusieurs sont cochés.</summary>
    public IReadOnlyList<string>? ArticlesExclus { get; init; }
    public IReadOnlyList<string>? TiersExclus { get; init; }
    public IReadOnlyList<int>? Commerciaux { get; init; }
    public IReadOnlyList<int>? CommerciauxExclus { get; init; }
    public IReadOnlyList<int>? Depots { get; init; }
    public IReadOnlyList<int>? DepotsExclus { get; init; }
}

/// <summary>
/// Lectures du tableau de bord. Toutes en SQL, en lecture seule : aucune ne modifie Sage.
/// Elles lisent sans poser de verrou (READ UNCOMMITTED) pour ne jamais ralentir les utilisateurs de Sage pendant l'actualisation.
/// </summary>
public interface ILecturesTableauDeBord
{
    Task<IReadOnlyList<Exercice>> Exercices();
    Task<IReadOnlyList<FaitCompta>> Compta(DateTime depuis);
    Task<IReadOnlyList<FaitAnalytique>> Analytique(DateTime depuis);
    Task<IReadOnlyList<FaitLigne>> Lignes(DateTime depuis);
    Task<IReadOnlyList<FaitPiece>> Pieces(DateTime depuis);
    Task<IReadOnlyList<FaitEnCours>> EnCours();
    Task<IReadOnlyList<FaitCommande>> Commandes(DateTime depuis);
    Task<IReadOnlyList<LigneStock>> Stock();
    /// <summary>Dernière sortie de stock (livraison ou facture de vente, mouvement de sortie) par article.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> DernieresSorties();
    Task<IReadOnlyList<EcheanceTiers>> Echeances();
    Task<IReadOnlyList<FaitReglement>> Reglements(DateTime depuis);
    Task<IReadOnlyList<RefCompte>> Comptes();
    Task<IReadOnlyList<RefJournal>> Journaux();
    Task<IReadOnlyList<RefTiers>> Tiers();
    Task<IReadOnlyList<RefCode>> Plans();
    Task<IReadOnlyList<RefSection>> Sections();
    Task<IReadOnlyList<RefArticle>> Articles();
    Task<IReadOnlyList<RefFamille>> Familles();
    Task<IReadOnlyList<RefCode>> Depots();
    Task<IReadOnlyList<RefCode>> Collaborateurs();
    Task<IReadOnlyList<RefCode>> ModesReglement();
    Task<IReadOnlyList<RefCode>> CategoriesTarifaires();
    /// <summary>Bons de fabrication et mouvements de sortie (documents de stock 26 et 21).</summary>
    Task<IReadOnlyList<FaitMouvement>> Mouvements(DateTime depuis);
    Task<IReadOnlyList<LigneNomenclature>> Nomenclatures();
    Task<IReadOnlyList<RefFournisseurArticle>> FournisseursArticles();
    /// <summary>Réceptions des commandes fournisseurs (BL et factures d'achat issus d'un bon de commande).</summary>
    Task<IReadOnlyList<FaitReception>> Receptions(DateTime depuis);
    Task<IReadOnlyList<RefArticleProduction>> ArticlesProduction();
    Task<IReadOnlyList<DetailEcriture>> DetailCompta(FiltreDetailCompta f);
    Task<IReadOnlyList<DetailLigne>> DetailVentes(FiltreDetailVentes f);
}

public sealed class LecturesTableauDeBordSql(Dossiers dossiers, IOptionsMonitor<TableauDeBordOptions> options) : ILecturesTableauDeBord
{
    const string SansVerrou = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; ";
    // Journaux de situation (JO_Type 4) exclus : ce ne sont pas des écritures réelles.
    const string JournauxReels = "JOIN F_JOURNAUX j ON j.JO_Num = e.JO_Num AND j.JO_Type <> 4 ";
    // Signe d'une pièce : facture de retour et d'avoir (DO_Provenance 1, 2), bon de retour et d'avoir financier en négatif.
    const string Signe = "CASE WHEN e.DO_Provenance IN (1, 2) OR e.DO_Type IN (4, 5, 14, 15) THEN -1 ELSE 1 END";
    // Factures, et les bons que le réglage DocumentsCa peut ajouter : tous sont lus, le tri se fait à l'affichage.
    const string Documents = "((e.DO_Domaine = 0 AND e.DO_Type IN (3, 4, 5, 6, 7)) OR (e.DO_Domaine = 1 AND e.DO_Type IN (13, 14, 15, 16, 17)))";

    SqlConnection Cnx() => new(dossiers.Adapter(options.CurrentValue.ChaineSql));
    int Delai => Math.Max(30, options.CurrentValue.DelaiSqlSecondes);

    async Task<IReadOnlyList<T>> Lire<T>(string sql, object? p = null)
    {
        using var c = Cnx();
        return (await c.QueryAsync<T>(SansVerrou + sql, p, commandTimeout: Delai)).AsList();
    }

    public async Task<IReadOnlyList<Exercice>> Exercices()
    {
        using var c = Cnx();
        var d = await c.QueryFirstOrDefaultAsync(SansVerrou +
            "SELECT TOP 1 D_DebutExo01, D_DebutExo02, D_DebutExo03, D_DebutExo04, D_DebutExo05, " +
            "D_FinExo01, D_FinExo02, D_FinExo03, D_FinExo04, D_FinExo05 FROM P_DOSSIER", commandTimeout: Delai) as IDictionary<string, object>;
        var liste = new List<Exercice>();
        if (d == null) return liste;
        for (var i = 1; i <= 5; i++)
        {
            if (d[$"D_DebutExo0{i}"] is DateTime debut && d[$"D_FinExo0{i}"] is DateTime fin && debut.Year > 1900 && fin > debut)
                liste.Add(new Exercice(i, debut.Date, fin.Date));
        }
        return liste.OrderByDescending(e => e.Debut).ToList();
    }

    public Task<IReadOnlyList<FaitCompta>> Compta(DateTime depuis) => Lire<FaitCompta>(
        "SELECT e.JM_Date AS Mois, DATEADD(day, e.EC_Jour - 1, e.JM_Date) AS Date, e.CG_Num AS Compte, e.JO_Num AS Journal, NULLIF(e.CT_Num, '') AS Tiers, " +
        "CAST(CASE WHEN e.EC_ANType <> 0 THEN 1 ELSE 0 END AS bit) AS ANouveau, " +
        "CAST(SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS decimal(18,2)) AS Debit, " +
        "CAST(SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS decimal(18,2)) AS Credit, COUNT(*) AS Nombre " +
        "FROM F_ECRITUREC e " + JournauxReels +
        "WHERE e.JM_Date >= @depuis " +
        "GROUP BY e.JM_Date, e.EC_Jour, e.CG_Num, e.JO_Num, NULLIF(e.CT_Num, ''), CASE WHEN e.EC_ANType <> 0 THEN 1 ELSE 0 END", new { depuis });

    public Task<IReadOnlyList<FaitAnalytique>> Analytique(DateTime depuis) => Lire<FaitAnalytique>(
        "SELECT e.JM_Date AS Mois, DATEADD(day, e.EC_Jour - 1, e.JM_Date) AS Date, CAST(a.N_Analytique AS int) AS [Plan], a.CA_Num AS Section, e.CG_Num AS Compte, e.JO_Num AS Journal, " +
        "CAST(SUM(CASE WHEN e.EC_Sens = 0 THEN a.EA_Montant ELSE -a.EA_Montant END) AS decimal(18,2)) AS Montant " +
        "FROM F_ECRITUREA a JOIN F_ECRITUREC e ON e.EC_No = a.EC_No " + JournauxReels +
        "WHERE e.JM_Date >= @depuis AND e.EC_ANType = 0 " +
        "GROUP BY e.JM_Date, e.EC_Jour, a.N_Analytique, a.CA_Num, e.CG_Num, e.JO_Num", new { depuis });

    public Task<IReadOnlyList<FaitLigne>> Lignes(DateTime depuis) => Lire<FaitLigne>(
        "SELECT CAST(e.DO_Domaine AS int) AS Domaine, CAST(e.DO_Type AS int) AS Type, CAST(e.DO_Provenance AS int) AS Provenance, DATEFROMPARTS(YEAR(e.DO_Date), MONTH(e.DO_Date), 1) AS Mois, e.DO_Date AS Date, l.AR_Ref AS Article, " +
        "e.DO_Tiers AS Tiers, NULLIF(e.CO_No, 0) AS Commercial, NULLIF(l.DE_No, 0) AS Depot, " +
        $"CAST(SUM({Signe} * l.DL_MontantHT) AS decimal(18,2)) AS MontantHT, " +
        $"CAST(SUM({Signe} * ABS(l.DL_Qte)) AS decimal(18,4)) AS Quantite, " +
        $"CAST(SUM({Signe} * ABS(l.DL_Qte) * l.DL_PrixRU) AS decimal(18,2)) AS Cout " +
        "FROM F_DOCLIGNE l JOIN F_DOCENTETE e ON e.DO_Domaine = l.DO_Domaine AND e.DO_Type = l.DO_Type AND e.DO_Piece = l.DO_Piece " +
        $"WHERE {Documents} AND e.DO_Date >= @depuis AND l.AR_Ref <> '' " +
        "GROUP BY e.DO_Domaine, e.DO_Type, e.DO_Provenance, e.DO_Date, l.AR_Ref, e.DO_Tiers, NULLIF(e.CO_No, 0), NULLIF(l.DE_No, 0)",
        new { depuis });

    public Task<IReadOnlyList<FaitPiece>> Pieces(DateTime depuis) => Lire<FaitPiece>(
        "SELECT CAST(e.DO_Domaine AS int) AS Domaine, CAST(e.DO_Type AS int) AS Type, CAST(e.DO_Provenance AS int) AS Provenance, e.DO_Date AS Date, e.DO_Piece AS Piece, e.DO_Tiers AS Tiers, NULLIF(e.CO_No, 0) AS Commercial, " +
        $"NULLIF(e.DE_No, 0) AS Depot, CAST({Signe} * e.DO_TotalHT AS decimal(18,2)) AS MontantHT, " +
        "CAST(CASE WHEN e.DO_Provenance IN (1, 2) OR e.DO_Type IN (4, 5, 14, 15) THEN 1 ELSE 0 END AS bit) AS Avoir " +
        $"FROM F_DOCENTETE e WHERE {Documents} AND e.DO_Date >= @depuis", new { depuis });

    public Task<IReadOnlyList<FaitEnCours>> EnCours() => Lire<FaitEnCours>(
        "SELECT CAST(e.DO_Type AS int) AS Type, e.DO_Date AS Date, NULLIF(e.DO_DateLivr, '1900-01-01') AS DateLivraison, e.DO_Piece AS Piece, " +
        "e.DO_Tiers AS Tiers, NULLIF(e.CO_No, 0) AS Commercial, CAST(e.DO_TotalHT AS decimal(18,2)) AS MontantHT " +
        "FROM F_DOCENTETE e WHERE e.DO_Domaine = 0 AND e.DO_Type IN (0, 1, 2, 3) AND e.DO_Cloture = 0");

    // Commandes clients et ce qu'elles sont devenues. Les factures de retour et d'avoir (DO_Provenance 1, 2) n'en font pas partie.
    public Task<IReadOnlyList<FaitCommande>> Commandes(DateTime depuis) => Lire<FaitCommande>(
        "SELECT x.Commande, x.DateCommande, e.DO_Piece AS Piece, CAST(e.DO_Type AS int) AS Type, " +
        "CAST(CASE WHEN e.DO_Cloture <> 0 THEN 1 ELSE 0 END AS bit) AS Cloture, x.DateLivraison, l.AR_Ref AS Article, " +
        "e.DO_Tiers AS Tiers, NULLIF(e.CO_No, 0) AS Commercial, NULLIF(l.DE_No, 0) AS Depot, " +
        "CAST(SUM(ABS(l.DL_Qte)) AS decimal(18,4)) AS Quantite, CAST(SUM(l.DL_MontantHT) AS decimal(18,2)) AS MontantHT " +
        "FROM F_DOCLIGNE l JOIN F_DOCENTETE e ON e.DO_Domaine = l.DO_Domaine AND e.DO_Type = l.DO_Type AND e.DO_Piece = l.DO_Piece " +
        "CROSS APPLY (SELECT CASE WHEN e.DO_Type = 1 THEN e.DO_Piece ELSE l.DL_PieceBC END AS Commande, " +
        "CASE WHEN e.DO_Type = 1 THEN e.DO_Date ELSE COALESCE(NULLIF(l.DL_DateBC, '1900-01-01'), e.DO_Date) END AS DateCommande, " +
        "CASE WHEN e.DO_Type IN (3, 6, 7) THEN COALESCE(NULLIF(l.DL_DateBL, '1900-01-01'), e.DO_Date) END AS DateLivraison) x " +
        "WHERE e.DO_Domaine = 0 AND l.AR_Ref <> '' AND e.DO_Provenance NOT IN (1, 2) " +
        "AND (e.DO_Type = 1 OR (e.DO_Type IN (2, 3, 6, 7) AND l.DL_PieceBC <> '')) AND x.DateCommande >= @depuis " +
        "GROUP BY x.Commande, x.DateCommande, e.DO_Piece, e.DO_Type, e.DO_Cloture, x.DateLivraison, l.AR_Ref, e.DO_Tiers, NULLIF(e.CO_No, 0), NULLIF(l.DE_No, 0)",
        new { depuis });

    public Task<IReadOnlyList<LigneStock>> Stock() => Lire<LigneStock>(
        "SELECT s.AR_Ref AS Article, CAST(s.DE_No AS int) AS Depot, CAST(s.AS_QteSto AS decimal(18,4)) AS Quantite, " +
        "CAST(s.AS_MontSto AS decimal(18,2)) AS Valeur, CAST(s.AS_QteMini AS decimal(18,4)) AS Mini, CAST(s.AS_QteMaxi AS decimal(18,4)) AS Maxi, " +
        "CAST(s.AS_QteRes AS decimal(18,4)) AS Reserve, CAST(s.AS_QteCom AS decimal(18,4)) AS Commande " +
        "FROM F_ARTSTOCK s JOIN F_ARTICLE a ON a.AR_Ref = s.AR_Ref WHERE a.AR_Sommeil = 0");

    public async Task<IReadOnlyDictionary<string, DateTime>> DernieresSorties()
    {
        var r = await Lire<(string Article, DateTime Date)>(
            "SELECT l.AR_Ref, MAX(l.DO_Date) FROM F_DOCLIGNE l " +
            "WHERE l.AR_Ref <> '' AND ((l.DO_Domaine = 0 AND l.DO_Type IN (3, 6, 7)) OR (l.DO_Domaine = 2 AND l.DO_Type = 21)) GROUP BY l.AR_Ref");
        return r.ToDictionary(x => x.Article, x => x.Date, StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<EcheanceTiers>> Echeances() => Lire<EcheanceTiers>(
        // Écritures non lettrées des comptes de tiers (classe 4) des clients et fournisseurs.
        "SELECT CAST(t.CT_Type AS int) AS Type, e.CT_Num AS Tiers, e.JO_Num AS Journal, DATEADD(day, e.EC_Jour - 1, e.JM_Date) AS Date, " +
        "NULLIF(e.EC_Piece, '') AS Piece, NULLIF(e.EC_Intitule, '') AS Libelle, NULLIF(e.EC_Echeance, '1900-01-01') AS DateEcheance, " +
        "CAST(CASE WHEN e.EC_Sens = t.CT_Type THEN e.EC_Montant ELSE -e.EC_Montant END AS decimal(18,2)) AS Montant, " +
        "NULLIF(e.EC_DateRelance, '1900-01-01') AS DateRelance " +
        "FROM F_ECRITUREC e JOIN F_COMPTET t ON t.CT_Num = e.CT_Num AND t.CT_Type IN (0, 1) " + JournauxReels +
        "WHERE e.EC_Lettre = 0 AND e.CG_Num LIKE '4%'");

    public Task<IReadOnlyList<FaitReglement>> Reglements(DateTime depuis) => Lire<FaitReglement>(
        "SELECT DATEFROMPARTS(YEAR(r.RG_Date), MONTH(r.RG_Date), 1) AS Mois, CAST(r.RG_Type AS int) AS Type, CAST(r.N_Reglement AS int) AS Mode, " +
        "NULLIF(r.CT_NumPayeur, '') AS Tiers, CAST(SUM(r.RG_Montant) AS decimal(18,2)) AS Montant, COUNT(*) AS Nombre " +
        "FROM F_CREGLEMENT r WHERE r.RG_Date >= @depuis AND r.RG_Type IN (0, 1) " +
        "GROUP BY DATEFROMPARTS(YEAR(r.RG_Date), MONTH(r.RG_Date), 1), r.RG_Type, r.N_Reglement, NULLIF(r.CT_NumPayeur, '')", new { depuis });

    public Task<IReadOnlyList<RefCompte>> Comptes() =>
        Lire<RefCompte>("SELECT CG_Num AS Numero, CG_Intitule AS Intitule FROM F_COMPTEG");

    public Task<IReadOnlyList<RefJournal>> Journaux() =>
        Lire<RefJournal>("SELECT JO_Num AS Code, JO_Intitule AS Intitule, CAST(JO_Type AS int) AS Type FROM F_JOURNAUX");

    public Task<IReadOnlyList<RefTiers>> Tiers() => Lire<RefTiers>(
        "SELECT CT_Num AS Numero, CT_Intitule AS Intitule, CAST(CT_Type AS int) AS Type, NULLIF(CO_No, 0) AS Representant, " +
        "NULLIF(CAST(N_CatTarif AS int), 0) AS Categorie, NULLIF(LTRIM(RTRIM(CT_Qualite)), '') AS Qualite FROM F_COMPTET");

    public Task<IReadOnlyList<RefCode>> Plans() =>
        Lire<RefCode>("SELECT CAST(cbIndice AS int) AS Numero, A_Intitule AS Intitule FROM P_ANALYTIQUE WHERE A_Intitule <> ''");

    public Task<IReadOnlyList<RefSection>> Sections() =>
        Lire<RefSection>("SELECT CAST(N_Analytique AS int) AS [Plan], CA_Num AS Numero, CA_Intitule AS Intitule FROM F_COMPTEA");

    public Task<IReadOnlyList<RefArticle>> Articles() =>
        Lire<RefArticle>("SELECT AR_Ref AS Reference, AR_Design AS Designation, NULLIF(FA_CodeFamille, '') AS Famille FROM F_ARTICLE");

    public Task<IReadOnlyList<RefFamille>> Familles() =>
        Lire<RefFamille>("SELECT FA_CodeFamille AS Code, FA_Intitule AS Intitule FROM F_FAMILLE");

    public Task<IReadOnlyList<RefCode>> Depots() =>
        Lire<RefCode>("SELECT CAST(DE_No AS int) AS Numero, DE_Intitule AS Intitule FROM F_DEPOT");

    public Task<IReadOnlyList<RefCode>> Collaborateurs() => Lire<RefCode>(
        "SELECT CAST(CO_No AS int) AS Numero, LTRIM(RTRIM(ISNULL(CO_Prenom, '') + ' ' + CO_Nom)) AS Intitule FROM F_COLLABORATEUR");

    public Task<IReadOnlyList<RefCode>> ModesReglement() =>
        Lire<RefCode>("SELECT CAST(cbIndice AS int) AS Numero, R_Intitule AS Intitule FROM P_REGLEMENT WHERE R_Intitule <> ''");

    public Task<IReadOnlyList<RefCode>> CategoriesTarifaires() =>
        Lire<RefCode>("SELECT CAST(cbIndice AS int) AS Numero, CT_Intitule AS Intitule FROM P_CATTARIF WHERE CT_Intitule <> ''");

    // Ligne du composé : elle entre en stock (DL_MvtStock 1, 2). Valeur : montant de la ligne, sinon quantité × prix de revient.
    public Task<IReadOnlyList<FaitMouvement>> Mouvements(DateTime depuis) => Lire<FaitMouvement>(
        "SELECT CAST(e.DO_Type AS int) AS Type, e.DO_Date AS Date, e.DO_Piece AS Piece, NULLIF(l.AR_RefCompose, '') AS Compose, l.AR_Ref AS Article, " +
        "NULLIF(l.DE_No, 0) AS Depot, CAST(CASE WHEN l.DL_MvtStock IN (1, 2) THEN 1 ELSE 0 END AS bit) AS Entree, " +
        "CAST(SUM(ABS(l.DL_Qte)) AS decimal(18,4)) AS Quantite, " +
        "CAST(SUM(CASE WHEN l.DL_MontantHT <> 0 THEN ABS(l.DL_MontantHT) ELSE ABS(l.DL_Qte * l.DL_PrixRU) END) AS decimal(18,2)) AS Valeur " +
        "FROM F_DOCLIGNE l JOIN F_DOCENTETE e ON e.DO_Domaine = l.DO_Domaine AND e.DO_Type = l.DO_Type AND e.DO_Piece = l.DO_Piece " +
        "WHERE e.DO_Domaine = 2 AND e.DO_Type IN (21, 26) AND e.DO_Date >= @depuis AND l.AR_Ref <> '' " +
        "GROUP BY e.DO_Type, e.DO_Date, e.DO_Piece, NULLIF(l.AR_RefCompose, ''), l.AR_Ref, NULLIF(l.DE_No, 0), CASE WHEN l.DL_MvtStock IN (1, 2) THEN 1 ELSE 0 END",
        new { depuis });

    // Gammes du composé : une nomenclature par combinaison. Sans ces colonnes (version plus ancienne), une seule nomenclature par article.
    public async Task<IReadOnlyList<LigneNomenclature>> Nomenclatures()
    {
        string Requete(string gammes) =>
            "SELECT n.AR_Ref AS Compose, n.NO_RefDet AS Composant, CAST(n.NO_Qte AS decimal(18,6)) AS Quantite, " +
            "CAST(CASE WHEN n.NO_Type = 0 THEN 1 ELSE 0 END AS bit) AS Fixe, " +
            "CAST(CASE WHEN a.AR_QteComp > 0 THEN a.AR_QteComp ELSE 1 END AS decimal(18,6)) AS QteComposition, " + gammes +
            " FROM F_NOMENCLAT n JOIN F_ARTICLE a ON a.AR_Ref = n.AR_Ref WHERE n.NO_RefDet <> ''";
        try
        {
            return await Lire<LigneNomenclature>(Requete("CAST(ISNULL(n.AG_No1Comp, 0) AS int) AS Gamme1, CAST(ISNULL(n.AG_No2Comp, 0) AS int) AS Gamme2"));
        }
        catch (SqlException)
        {
            return await Lire<LigneNomenclature>(Requete("0 AS Gamme1, 0 AS Gamme2"));
        }
    }

    public Task<IReadOnlyList<RefFournisseurArticle>> FournisseursArticles() => Lire<RefFournisseurArticle>(
        "SELECT f.AR_Ref AS Article, f.CT_Num AS Fournisseur, CAST(CASE WHEN f.AF_Principal <> 0 THEN 1 ELSE 0 END AS bit) AS Principal, " +
        "CAST(f.AF_DelaiAppro AS int) AS Delai, CAST(f.AF_QteMini AS decimal(18,4)) AS QteMini, CAST(f.AF_Colisage AS decimal(18,4)) AS Colisage, " +
        "CAST(f.AF_PrixAch AS decimal(18,4)) AS Prix FROM F_ARTFOURNISS f WHERE f.CT_Num <> ''");

    public Task<IReadOnlyList<FaitReception>> Receptions(DateTime depuis) => Lire<FaitReception>(
        "SELECT l.AR_Ref AS Article, e.DO_Tiers AS Fournisseur, l.DL_DateBC AS DateCommande, " +
        "COALESCE(NULLIF(l.DL_DateBL, '1900-01-01'), e.DO_Date) AS DateReception, CAST(SUM(ABS(l.DL_Qte)) AS decimal(18,4)) AS Quantite " +
        "FROM F_DOCLIGNE l JOIN F_DOCENTETE e ON e.DO_Domaine = l.DO_Domaine AND e.DO_Type = l.DO_Type AND e.DO_Piece = l.DO_Piece " +
        "WHERE e.DO_Domaine = 1 AND e.DO_Type IN (13, 16, 17) AND e.DO_Provenance NOT IN (1, 2) AND l.AR_Ref <> '' AND l.DL_PieceBC <> '' " +
        "AND l.DL_DateBC > '1900-01-01' AND e.DO_Date >= @depuis " +
        "GROUP BY l.AR_Ref, e.DO_Tiers, l.DL_DateBC, COALESCE(NULLIF(l.DL_DateBL, '1900-01-01'), e.DO_Date)", new { depuis });

    public Task<IReadOnlyList<RefArticleProduction>> ArticlesProduction() => Lire<RefArticleProduction>(
        "SELECT AR_Ref AS Reference, CAST(AR_Nomencl AS int) AS Nomenclature, CAST(AR_PrixAch AS decimal(18,4)) AS PrixAchat, " +
        "CAST(AR_PUNet AS decimal(18,4)) AS DernierPrix, CAST(AR_DelaiFabrication AS int) AS DelaiFabrication, CAST(AR_Nature AS int) AS Nature FROM F_ARTICLE");

    public Task<IReadOnlyList<DetailEcriture>> DetailCompta(FiltreDetailCompta f)
    {
        var p = new DynamicParameters(new { du = f.Du, au = f.Au, duMois = Periodes.DebutMois(f.Du), tiers = f.Tiers, taille = f.Taille });
        var sql = "SELECT TOP (@taille) DATEADD(day, e.EC_Jour - 1, e.JM_Date) AS Date, e.JO_Num AS Journal, NULLIF(e.EC_Piece, '') AS Piece, " +
            "e.CG_Num AS Compte, NULLIF(e.CT_Num, '') AS Tiers, NULLIF(e.EC_Intitule, '') AS Libelle, NULLIF(e.EC_Echeance, '1900-01-01') AS Echeance, " +
            "CAST(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END AS decimal(18,2)) AS Debit, " +
            "CAST(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END AS decimal(18,2)) AS Credit, NULLIF(e.EC_Lettrage, '') AS Lettrage " +
            "FROM F_ECRITUREC e " + JournauxReels +
            "WHERE e.JM_Date >= @duMois AND e.JM_Date <= @au AND DATEADD(day, e.EC_Jour - 1, e.JM_Date) BETWEEN @du AND @au AND (@tiers IS NULL OR e.CT_Num = @tiers) " +
            (f.ANouveaux ? "" : "AND e.EC_ANType = 0 ");
        sql += Prefixes("e.CG_Num", f.Comptes, p, "cpt") + Liste("e.JO_Num", f.Journaux, p, "jo") + Liste("e.CT_Num", f.TiersListe, p, "ti")
            + Prefixes("e.CG_Num", f.ComptesExclus, p, "cptx", exclure: true) + Liste("e.JO_Num", f.JournauxExclus, p, "jox", exclure: true)
            + Liste("e.CT_Num", f.TiersExclus, p, "tix", exclure: true);
        return Lire<DetailEcriture>(sql + " ORDER BY e.JM_Date, e.EC_Jour, e.EC_No", p);
    }

    public Task<IReadOnlyList<DetailLigne>> DetailVentes(FiltreDetailVentes f)
    {
        var p = new DynamicParameters(new { domaine = f.Domaine, du = f.Du, au = f.Au.AddDays(1),
            article = f.Article, tiers = f.Tiers, commercial = f.Commercial, depot = f.Depot, taille = f.Taille });
        var sql = "SELECT TOP (@taille) e.DO_Date AS Date, e.DO_Piece AS Piece, e.DO_Tiers AS Tiers, l.AR_Ref AS Article, l.DL_Design AS Designation, " +
            $"CAST({Signe} * ABS(l.DL_Qte) AS decimal(18,4)) AS Quantite, CAST({Signe} * l.DL_MontantHT AS decimal(18,2)) AS MontantHT, " +
            $"CAST({Signe} * ABS(l.DL_Qte) * l.DL_PrixRU AS decimal(18,2)) AS Cout " +
            "FROM F_DOCLIGNE l JOIN F_DOCENTETE e ON e.DO_Domaine = l.DO_Domaine AND e.DO_Type = l.DO_Type AND e.DO_Piece = l.DO_Piece " +
            $"WHERE e.DO_Domaine = @domaine AND {(f.Documents ?? DocumentsCa.Defaut).Condition(f.Domaine)} AND e.DO_Date >= @du AND e.DO_Date < @au AND l.AR_Ref <> '' " +
            "AND (@article IS NULL OR l.AR_Ref = @article) AND (@tiers IS NULL OR e.DO_Tiers = @tiers) " +
            "AND (@commercial IS NULL OR e.CO_No = @commercial) AND (@depot IS NULL OR l.DE_No = @depot) ";
        sql += Liste("l.AR_Ref", f.Articles, p, "ar") + Liste("e.DO_Tiers", f.TiersListe, p, "ti")
            + Liste("l.AR_Ref", f.ArticlesExclus, p, "arx", exclure: true) + Liste("e.DO_Tiers", f.TiersExclus, p, "tix", exclure: true)
            + Liste("e.CO_No", f.Commerciaux, p, "co") + Liste("e.CO_No", f.CommerciauxExclus, p, "cox", exclure: true)
            + Liste("l.DE_No", f.Depots, p, "de") + Liste("l.DE_No", f.DepotsExclus, p, "dex", exclure: true);
        return Lire<DetailLigne>(sql + " ORDER BY e.DO_Date DESC, e.DO_Piece", p);
    }

    static string Liste<T>(string colonne, IReadOnlyList<T>? valeurs, DynamicParameters p, string nom, bool exclure = false)
    {
        if (valeurs is not { Count: > 0 }) return "";
        p.Add(nom, valeurs);
        return $" AND {colonne} {(exclure ? "NOT IN" : "IN")} @{nom}";
    }

    static string Prefixes(string colonne, IReadOnlyList<string>? prefixes, DynamicParameters p, string nom, bool exclure = false)
    {
        if (prefixes is not { Count: > 0 }) return "";
        var conditions = prefixes.Select((x, i) =>
        {
            p.Add($"{nom}{i}", x.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%");
            return $"{colonne} LIKE @{nom}{i}";
        });
        return (exclure ? " AND NOT (" : " AND (") + string.Join(" OR ", conditions) + ")";
    }
}
