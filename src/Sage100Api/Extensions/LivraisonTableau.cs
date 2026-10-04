using Dapper;

namespace Sage100Api.Extensions;

/// <summary>Filtres du tableau de bord. Statut : celui de l'arrêt (a-livrer, livre, partiel, echec).</summary>
public sealed record FiltreTableau(DateTime? Du, DateTime? Au, int? Livreur, int? Depot, string? Client, string? Statut);

public sealed record LigneTableau
{
    public DateTime Date { get; init; }
    public string Tournee { get; init; } = "";
    public string? NomTournee { get; init; }
    public int? Livreur { get; init; }
    public int? Depot { get; init; }
    public string Piece { get; init; } = "";
    public string Client { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Ville { get; init; }
    public string Statut { get; init; } = "";
    public string? Motif { get; init; }
    public DateTime? Heure { get; init; }
    public decimal TotalTTC { get; init; }
    public string? Receptionnaire { get; init; }
}

public sealed record Compte(string Cle, int Nombre);

public sealed record SerieJour(DateTime Date, int Livres, int Partiels, int Echecs, int ALivrer);

public sealed record StatLivreur(int? Livreur, int Tournees, int Arrets, int Livres, int Partiels, int Echecs, decimal MontantLivre);

public sealed record IndicateursLivraison(int Tournees, int Arrets, int Livres, int Partiels, int Echecs, int ALivrer, double TauxReussite,
    decimal MontantLivre, decimal MontantNonLivre, int Courses, int CoursesFaites, int CoursesEnAttente, int ChargementsControles, int EcartsChargement,
    int LignesNonLivrees);

/// <summary>Tableau de bord : indicateurs, séries et liste détaillée, sur les mêmes filtres.</summary>
public sealed record TableauDeBord(IndicateursLivraison Indicateurs, IReadOnlyList<SerieJour> ParJour, IReadOnlyList<StatLivreur> ParLivreur,
    IReadOnlyList<Compte> Motifs, IReadOnlyList<Compte> CoursesParStatut, IReadOnlyList<Compte> ParVille, IReadOnlyList<LigneTableau> Arrets,
    IReadOnlyList<int> Livreurs, IReadOnlyList<int> Depots);

public sealed partial class Livraison
{
    public TableauDeBord Tableau(FiltreTableau f, int taille = 500)
    {
        using var c = Ouvrir();
        var p = new
        {
            du = f.Du?.ToString("yyyy-MM-dd"), au = f.Au?.ToString("yyyy-MM-dd"), livreur = f.Livreur, depot = f.Depot,
            client = string.IsNullOrWhiteSpace(f.Client) ? null : $"%{f.Client.Trim().ToUpperInvariant()}%", statut = f.Statut,
        };
        const string FiltreTournees = "(@du IS NULL OR t.date >= @du) AND (@au IS NULL OR t.date <= @au) AND (@livreur IS NULL OR t.livreur = @livreur) " +
            "AND (@depot IS NULL OR t.depot = @depot)";
        const string FiltreArrets = FiltreTournees + " AND (@client IS NULL OR UPPER(a.client) LIKE @client OR UPPER(COALESCE(a.intitule, '')) LIKE @client) " +
            "AND (@statut IS NULL OR a.statut = @statut)";

        var lignes = c.Query<Brut>(
            "SELECT t.date AS Date, t.id AS Tournee, t.nom AS NomTournee, t.livreur AS Livreur, t.depot AS Depot, a.piece AS Piece, a.client AS Client, " +
            "a.intitule AS Intitule, a.ville AS Ville, a.statut AS Statut, a.motif AS Motif, a.heure AS Heure, a.total_ttc AS TotalTTC, a.receptionnaire AS Receptionnaire " +
            $"FROM arrets a JOIN tournees t ON t.id = a.tournee WHERE {FiltreArrets} ORDER BY t.date DESC, t.id, a.ordre", p)
            .Select(b => b.Ligne()).ToList();

        var courses = c.Query<(string Statut, long Nombre)>(
            $"SELECT k.statut, COUNT(*) FROM courses k JOIN tournees t ON t.id = k.tournee WHERE {FiltreTournees} GROUP BY k.statut", p)
            .Select(x => new Compte(x.Statut, (int)x.Nombre)).OrderBy(x => Array.IndexOf(StatutsCourse, x.Cle)).ToList();
        var motifsLignes = c.Query<(string Motif, long Nombre)>(
            $"SELECT aa.motif, COUNT(*) FROM arret_articles aa JOIN arrets a ON a.tournee = aa.tournee AND a.piece = aa.piece JOIN tournees t ON t.id = a.tournee " +
            $"WHERE aa.motif IS NOT NULL AND {FiltreArrets} GROUP BY aa.motif", p).ToDictionary(x => x.Motif, x => (int)x.Nombre);
        var chargements = c.QueryFirst<(long Controles, long Ecarts)>(
            $"SELECT (SELECT COUNT(*) FROM chargements g JOIN tournees t ON t.id = g.tournee WHERE {FiltreTournees}), " +
            $"(SELECT COUNT(*) FROM arret_articles aa JOIN tournees t ON t.id = aa.tournee WHERE aa.qte_chargee IS NOT NULL AND aa.qte_chargee <> aa.quantite AND {FiltreTournees})", p);
        var lignesNonLivrees = c.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM arret_articles aa JOIN arrets a ON a.tournee = aa.tournee AND a.piece = aa.piece JOIN tournees t ON t.id = a.tournee " +
            $"WHERE aa.qte_livree IS NOT NULL AND aa.qte_livree < COALESCE(aa.qte_chargee, aa.quantite) AND {FiltreArrets}", p);

        int N(IEnumerable<LigneTableau> l, string statut) => l.Count(x => x.Statut == statut);
        var livres = N(lignes, "livre");
        var partiels = N(lignes, "partiel");
        var echecs = N(lignes, "echec");
        var traites = livres + partiels + echecs;
        var indicateurs = new IndicateursLivraison(
            lignes.Select(x => x.Tournee).Distinct().Count(), lignes.Count, livres, partiels, echecs, N(lignes, "a-livrer"),
            traites == 0 ? 0 : Math.Round(100.0 * livres / traites, 1),
            lignes.Where(x => x.Statut is "livre" or "partiel").Sum(x => x.TotalTTC), lignes.Where(x => x.Statut == "echec").Sum(x => x.TotalTTC),
            courses.Sum(x => x.Nombre), courses.Where(x => x.Cle == "fait").Sum(x => x.Nombre), courses.Where(x => x.Cle is "a-faire" or "en-cours").Sum(x => x.Nombre),
            (int)chargements.Controles, (int)chargements.Ecarts, (int)lignesNonLivrees);

        var motifs = lignes.Where(x => x.Motif != null).GroupBy(x => x.Motif!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (m, n) in motifsLignes) motifs[m] = motifs.GetValueOrDefault(m) + n;

        return new TableauDeBord(
            indicateurs,
            lignes.GroupBy(x => x.Date).OrderBy(g => g.Key)
                .Select(g => new SerieJour(g.Key, N(g, "livre"), N(g, "partiel"), N(g, "echec"), N(g, "a-livrer"))).ToList(),
            lignes.GroupBy(x => x.Livreur).Select(g => new StatLivreur(g.Key, g.Select(x => x.Tournee).Distinct().Count(), g.Count(), N(g, "livre"), N(g, "partiel"),
                N(g, "echec"), g.Where(x => x.Statut is "livre" or "partiel").Sum(x => x.TotalTTC))).OrderByDescending(x => x.Arrets).ToList(),
            motifs.Select(kv => new Compte(kv.Key, kv.Value)).OrderByDescending(x => x.Nombre).ToList(),
            courses,
            lignes.GroupBy(x => x.Ville ?? "—").Select(g => new Compte(g.Key, g.Count())).OrderByDescending(x => x.Nombre).Take(10).ToList(),
            lignes.Take(taille).ToList(),
            c.Query<long>("SELECT DISTINCT livreur FROM tournees WHERE livreur IS NOT NULL ORDER BY livreur").Select(x => (int)x).ToList(),
            c.Query<long>("SELECT DISTINCT depot FROM tournees WHERE depot IS NOT NULL ORDER BY depot").Select(x => (int)x).ToList());
    }

    sealed class Brut
    {
        public string Date { get; set; } = "";
        public string Tournee { get; set; } = "";
        public string? NomTournee { get; set; }
        public long? Livreur { get; set; }
        public long? Depot { get; set; }
        public string Piece { get; set; } = "";
        public string Client { get; set; } = "";
        public string? Intitule { get; set; }
        public string? Ville { get; set; }
        public string Statut { get; set; } = "";
        public string? Motif { get; set; }
        public string? Heure { get; set; }
        public double TotalTTC { get; set; }
        public string? Receptionnaire { get; set; }

        public LigneTableau Ligne() => new()
        {
            Date = DateTime.ParseExact(Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Tournee = Tournee, NomTournee = NomTournee,
            Livreur = (int?)Livreur, Depot = (int?)Depot, Piece = Piece, Client = Client, Intitule = Intitule, Ville = Ville, Statut = Statut, Motif = Motif,
            Heure = Livraison.Date(Heure), TotalTTC = Math.Round((decimal)TotalTTC, 2), Receptionnaire = Receptionnaire,
        };
    }
}
