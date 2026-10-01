using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sage100Api.Contracts;

namespace Sage100Api.Worker;

public interface IWorkerClient
{
    Task<WorkerResponse> Envoyer(string operation, object? donnees, CancellationToken ct = default);
}

/// <summary>Client du canal nommé vers le worker Objets Métiers (même machine).</summary>
public sealed class WorkerClient(IOptions<SageOptions> options, ILogger<WorkerClient> log) : IWorkerClient
{
    public async Task<WorkerResponse> Envoyer(string operation, object? donnees, CancellationToken ct = default)
    {
        var requete = new WorkerRequest
        {
            Operation = operation,
            Donnees = donnees == null ? null : JsonSerializer.SerializeToElement(donnees, WorkerProtocol.Json),
        };
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(TimeSpan.FromSeconds(options.Value.DelaiWorkerSecondes));
        try
        {
            await using var canal = new NamedPipeClientStream(".", WorkerProtocol.NomCanal, PipeDirection.InOut, PipeOptions.Asynchronous);
            await canal.ConnectAsync(5000, delai.Token);
            using var ecrivain = new StreamWriter(canal, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var lecteur = new StreamReader(canal, new UTF8Encoding(false), leaveOpen: true);
            await ecrivain.WriteLineAsync(JsonSerializer.Serialize(requete, WorkerProtocol.Json).AsMemory(), delai.Token);
            var ligne = await lecteur.ReadLineAsync(delai.Token);
            return ligne == null
                ? Indisponible("Le worker a fermé la connexion sans répondre.")
                : JsonSerializer.Deserialize<WorkerResponse>(ligne, WorkerProtocol.Json) ?? Indisponible("Réponse vide du worker.");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            log.LogWarning(ex, "Worker Objets Métiers injoignable pour {Operation}", operation);
            return Indisponible("Worker Objets Métiers injoignable : " + ex.Message);
        }
    }

    static WorkerResponse Indisponible(string message) =>
        new() { Ok = false, CodeErreur = CodesErreur.Technique, MessageErreur = message };
}
