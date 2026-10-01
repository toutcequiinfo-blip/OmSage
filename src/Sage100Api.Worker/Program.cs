// Worker Objets Métiers : écoute l'API sur un canal nommé et exécute les écritures Sage une par une.
// Lancement : Sage100Api.Worker.exe (lit worker.json à côté de l'exécutable), en console ou en service Windows.

using System;
using System.IO;
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
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Worker prêt sur le canal '{WorkerProtocol.NomCanal}' (base {config.BaseCial} sur {config.Serveur}).");
            // Ouvre la session Sage dès le démarrage : la première vente n'attend pas l'ouverture des bases.
            executeur.Soumettre(new WorkerRequest { Operation = Operations.Ping }).ContinueWith(t =>
                Console.WriteLine(t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result.Ok
                    ? "Session Sage ouverte."
                    : "Ouverture de la session Sage impossible : " + (t.Exception?.GetBaseException().Message ?? t.Result.MessageErreur)));
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
        /// <summary>Vide = sécurité intégrée Windows sur BaseCial.</summary>
        public string ChaineSql { get; set; } = "";

        public string ConnexionSql() => string.IsNullOrEmpty(ChaineSql)
            ? $"Server={Serveur};Database={BaseCial};Integrated Security=true;ApplicationIntent=ReadOnly"
            : ChaineSql;
    }
}
