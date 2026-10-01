using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sage100Api.Contracts;

namespace Sage100Api.Worker
{
    /// <summary>
    /// Serveur de canal nommé : accepte plusieurs connexions de l'API en parallèle, mais chaque requête est
    /// confiée à l'<see cref="ExecuteurSage"/>, qui les exécute une par une sur son thread COM.
    /// Un message = une ligne JSON (WorkerRequest), une réponse = une ligne JSON (WorkerResponse).
    /// </summary>
    internal sealed class ServeurCanal
    {
        readonly ExecuteurSage _executeur;

        public ServeurCanal(ExecuteurSage executeur) => _executeur = executeur;

        public void Executer()
        {
            while (true)
            {
                var canal = new NamedPipeServerStream(WorkerProtocol.NomCanal, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                canal.WaitForConnection();
                Task.Run(() => Servir(canal));
            }
        }

        void Servir(NamedPipeServerStream canal)
        {
            using (canal)
            using (var lecteur = new StreamReader(canal, new UTF8Encoding(false)))
            using (var ecrivain = new StreamWriter(canal, new UTF8Encoding(false)) { AutoFlush = true })
            {
                try
                {
                    string? ligne;
                    while ((ligne = lecteur.ReadLine()) != null)
                    {
                        WorkerResponse reponse;
                        try
                        {
                            var requete = JsonSerializer.Deserialize<WorkerRequest>(ligne, WorkerProtocol.Json)
                                          ?? throw new InvalidDataException("Requête vide.");
                            reponse = _executeur.Soumettre(requete).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            reponse = new WorkerResponse { Ok = false, CodeErreur = CodesErreur.Technique, MessageErreur = ex.Message };
                        }
                        ecrivain.WriteLine(JsonSerializer.Serialize(reponse, WorkerProtocol.Json));
                    }
                }
                catch (IOException)
                {
                    // Client déconnecté : rien à faire.
                }
            }
        }
    }
}
