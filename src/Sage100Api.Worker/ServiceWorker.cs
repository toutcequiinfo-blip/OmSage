using System.ServiceProcess;
using System.Threading;

namespace Sage100Api.Worker
{
    /// <summary>
    /// Le worker en service Windows « Sage100Api.Worker » (installé par deploy/installer.ps1).
    /// Même fonctionnement qu'en console ; la sortie console part dans logs\worker-AAAAMMJJ.log.
    /// </summary>
    internal sealed class ServiceWorker : ServiceBase
    {
        public const string Nom = "Sage100Api.Worker";

        readonly WorkerConfig _config;
        ExecuteurSage? _executeur;

        public ServiceWorker(WorkerConfig config)
        {
            _config = config;
            ServiceName = Nom;
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            _executeur = new ExecuteurSage(_config);
            var serveur = new ServeurCanal(_executeur);
            new Thread(serveur.Executer) { IsBackground = true, Name = "Canal" }.Start();
            Program.AnnoncerDemarrage(_executeur, _config);
        }

        protected override void OnStop() => Arreter();

        protected override void OnShutdown() => Arreter();

        void Arreter()
        {
            // Ferme proprement la session Sage (libère les verrous) avant l'arrêt du processus.
            _executeur?.Dispose();
            _executeur = null;
        }
    }
}
