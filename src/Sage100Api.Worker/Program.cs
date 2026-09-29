// Worker Objets Métiers : écoute l'API sur un canal nommé et exécute les écritures Sage une par une.
// Lancement : Sage100Api.Worker.exe (lit worker.json à côté de l'exécutable).

using System;
using System.IO;
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

            var chemin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker.json");
            var config = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(chemin), WorkerProtocol.Json)
                         ?? throw new InvalidOperationException("worker.json illisible.");

            using (var executeur = new ExecuteurSage(config))
            {
                var serveur = new ServeurCanal(executeur);
                Console.WriteLine($"Worker prêt sur le canal '{WorkerProtocol.NomCanal}' (base {config.BaseCial} sur {config.Serveur}). Ctrl+C pour arrêter.");
                serveur.Executer();
            }
            return 0;
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
