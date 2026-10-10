// Worker Objets Métiers : écoute l'API sur un canal nommé et exécute les écritures Sage une par une.
// Lancement : Sage100Api.Worker.exe (lit worker.json à côté de l'exécutable), en console ou en service Windows.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Sage100Api.Contracts;

namespace Sage100Api.Worker
{
    internal static class Program
    {
        static int Main()
        {
            if (Environment.Is64BitProcess)
            {
                Console.Error.WriteLine("Le worker doit tourner en 32 bits (x86) : la DLL Objets Métiers est 32 bits.");
                return 1;
            }

            var service = !Environment.UserInteractive;
            if (service) JournaliserDansFichier();

            var chemin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker.json");
            var config = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(chemin), WorkerProtocol.Json)
                         ?? throw new InvalidOperationException("worker.json illisible.");
            config.DevoilerSecrets();

            if (service)
            {
                ServiceBase.Run(new ServiceWorker(config));
                return 0;
            }

            using (var executeur = new ExecuteurSage(config))
            {
                var serveur = new ServeurCanal(executeur);
                AnnoncerDemarrage(executeur, config);
                Console.WriteLine("Ctrl+C pour arrêter.");
                serveur.Executer();
            }
            return 0;
        }

        internal static void AnnoncerDemarrage(ExecuteurSage executeur, WorkerConfig config)
        {
            var societes = config.Societes().ToList();
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Worker prêt sur le canal '{WorkerProtocol.NomCanal}' (base(s) {string.Join(", ", societes)} sur {config.Serveur}).");
            // Ouvre la session Sage de chaque société dès le démarrage : la première vente n'attend pas l'ouverture des bases.
            foreach (var societe in societes)
                executeur.Soumettre(new WorkerRequest { Operation = Operations.Ping, Dossier = societe }).ContinueWith(t =>
                    Console.WriteLine(t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result.Ok
                        ? $"Session Sage ouverte ({societe})."
                        : $"Ouverture de la session Sage impossible ({societe}) : " + (t.Exception?.GetBaseException().Message ?? t.Result.MessageErreur)));
        }

        /// <summary>En service, pas de console : tout part dans logs\worker-AAAAMMJJ.log, à côté de l'exécutable.</summary>
        static void JournaliserDansFichier()
        {
            var dossier = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dossier);
            var fichier = new StreamWriter(Path.Combine(dossier, $"worker-{DateTime.Now:yyyyMMdd}.log"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
            var sortie = TextWriter.Synchronized(fichier);
            Console.SetOut(sortie);
            Console.SetError(sortie);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => sortie.WriteLine($"[{DateTime.Now:HH:mm:ss}] Erreur fatale : {e.ExceptionObject}");
        }
    }

    public sealed class WorkerConfig
    {
        public string Serveur { get; set; } = "";
        public string BaseCial { get; set; } = "";
        public string BaseCpta { get; set; } = "";
        public string Utilisateur { get; set; } = "<Administrateur>";
        public string MotDePasse { get; set; } = "";
        /// <summary>
        /// Journal de trésorerie par mode de règlement, par exemple { "Espèces": "CAIS" }.
        /// Mode absent : Sage garde son journal par défaut.
        /// </summary>
        public Dictionary<string, string> JournauxParMode { get; set; } = new Dictionary<string, string>();
        /// <summary>Vide = sécurité intégrée Windows sur BaseCial.</summary>
        public string ChaineSql { get; set; } = "";

        /// <summary>
        /// Sociétés servies (plusieurs bases du même serveur), dans le même ordre que Sage:Dossiers de l'API.
        /// Vide : une seule société, BaseCial. Utilisateur, mot de passe et journaux absents : ceux du haut du fichier.
        /// </summary>
        public List<DossierWorker> Dossiers { get; set; } = new List<DossierWorker>();

        /// <summary>Mots de passe et chaînes SQL chiffrés par Windows (« dpapi:… », outils\securite.ps1) : déchiffrés au démarrage.</summary>
        public void DevoilerSecrets()
        {
            MotDePasse = Secrets.Devoiler(MotDePasse);
            ChaineSql = Secrets.Devoiler(ChaineSql);
            foreach (var d in Dossiers)
            {
                if (d.MotDePasse != null) d.MotDePasse = Secrets.Devoiler(d.MotDePasse);
                if (d.ChaineSql != null) d.ChaineSql = Secrets.Devoiler(d.ChaineSql);
            }
        }

        readonly Dictionary<string, WorkerConfig> _parSociete = new Dictionary<string, WorkerConfig>(StringComparer.OrdinalIgnoreCase);

        public string ConnexionSql() => string.IsNullOrEmpty(ChaineSql)
            ? $"Server={Serveur};Database={BaseCial};Integrated Security=true;ApplicationIntent=ReadOnly"
            : ChaineSql;

        /// <summary>Bases Gestion commerciale servies, la principale d'abord.</summary>
        public IEnumerable<string> Societes()
        {
            if (Dossiers.Count == 0) return new[] { BaseCial };
            return Dossiers.Select(d => d.BaseCial).Where(b => !string.IsNullOrWhiteSpace(b));
        }

        /// <summary>
        /// Configuration d'une société (code = base Gestion commerciale, comme dans l'API) ; null si le worker ne la connaît pas.
        /// Sans liste de sociétés, le worker ne sert que BaseCial : une demande pour une autre base est refusée,
        /// pour qu'une API réglée sur plusieurs sociétés n'écrive jamais dans la mauvaise base.
        /// </summary>
        public WorkerConfig? Pour(string? code)
        {
            if (Dossiers.Count == 0)
                return string.IsNullOrWhiteSpace(code) || string.Equals(code!.Trim(), BaseCial.Trim(), StringComparison.OrdinalIgnoreCase) ? this : null;
            var d = string.IsNullOrWhiteSpace(code)
                ? Dossiers.FirstOrDefault()
                : Dossiers.FirstOrDefault(x => string.Equals(x.BaseCial?.Trim(), code!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (d == null || string.IsNullOrWhiteSpace(d.BaseCial)) return null;
            if (_parSociete.TryGetValue(d.BaseCial, out var dejaFaite)) return dejaFaite;
            var personnalise = !string.IsNullOrEmpty(d.Utilisateur);
            var c = new WorkerConfig
            {
                Serveur = Serveur,
                BaseCial = d.BaseCial.Trim(),
                BaseCpta = string.IsNullOrWhiteSpace(d.BaseCpta) ? d.BaseCial.Trim() : d.BaseCpta!.Trim(),
                Utilisateur = personnalise ? d.Utilisateur! : Utilisateur,
                MotDePasse = personnalise ? d.MotDePasse ?? "" : MotDePasse,
                JournauxParMode = d.JournauxParMode ?? JournauxParMode,
                ChaineSql = d.ChaineSql ?? "",
            };
            _parSociete[d.BaseCial] = c;
            return c;
        }
    }

    /// <summary>Une société de worker.json (dossiers).</summary>
    public sealed class DossierWorker
    {
        public string BaseCial { get; set; } = "";
        /// <summary>Vide : la même base que la Gestion commerciale.</summary>
        public string? BaseCpta { get; set; }
        public string? Utilisateur { get; set; }
        public string? MotDePasse { get; set; }
        /// <summary>Journaux de trésorerie propres à cette société ; absents : ceux du haut du fichier.</summary>
        public Dictionary<string, string>? JournauxParMode { get; set; }
        public string? ChaineSql { get; set; }
    }
}
