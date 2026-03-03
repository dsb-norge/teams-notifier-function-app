using System.Diagnostics;
using System.Net.Sockets;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Fixtures;

/// <summary>
/// xUnit collection fixture that manages an Azurite instance for integration tests.
///
/// If Azurite is already running (e.g. started by setup-local.sh for local debugging),
/// the fixture reuses it. Otherwise, it starts a dedicated instance.
///
/// Each test class gets isolated tables/queues via a random suffix to avoid cross-class interference.
/// </summary>
public class AzuriteFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;" +
        "QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;" +
        "TableEndpoint=http://127.0.0.1:10002/devstoreaccount1";

    private const int TablePort = 10002;
    private const int QueuePort = 10001;
    private const int BlobPort = 10000;
    private const string AzuriteDataDir = "/tmp/azurite-integration-tests";

    private Process? _azuriteProcess;
    private bool _weStartedAzurite;

    private readonly List<TableClient> _trackedTables = [];
    private readonly List<QueueClient> _trackedQueues = [];
    private readonly string _suffix = GenerateSuffix();

    public string Suffix => _suffix;

    public async ValueTask InitializeAsync()
    {
        if (IsPortOpen(TablePort))
        {
            // Azurite already running (e.g. via setup-local.sh) — reuse it
            return;
        }

        // Start our own Azurite
        Directory.CreateDirectory(AzuriteDataDir);

        _azuriteProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "azurite",
                Arguments = $"--silent --skipApiVersionCheck --location {AzuriteDataDir} " +
                            $"--blobPort {BlobPort} --queuePort {QueuePort} --tablePort {TablePort}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _azuriteProcess.Start();
        _weStartedAzurite = true;

        // Wait for table service to become ready
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (IsPortOpen(TablePort))
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException("Azurite did not become ready within 15 seconds");
    }

    public async ValueTask DisposeAsync()
    {
        // Clean up tracked tables and queues
        foreach (var table in _trackedTables)
        {
            try { await table.DeleteAsync(); } catch { /* best-effort */ }
        }
        foreach (var queue in _trackedQueues)
        {
            try { await queue.DeleteAsync(); } catch { /* best-effort */ }
        }

        // Kill Azurite only if we started it
        if (_weStartedAzurite && _azuriteProcess is { HasExited: false })
        {
            _azuriteProcess.Kill(entireProcessTree: true);
            await _azuriteProcess.WaitForExitAsync();
            _azuriteProcess.Dispose();
        }
    }

    /// <summary>
    /// Creates a table with an isolated name ({baseName}{suffix}) and tracks it for cleanup.
    /// </summary>
    public TableClient CreateTableClient(string baseName)
    {
        var tableName = $"{baseName}{_suffix}";
        var client = new TableClient(ConnectionString, tableName);
        client.CreateIfNotExists();
        _trackedTables.Add(client);
        return client;
    }

    /// <summary>
    /// Creates a queue with an isolated name ({baseName}-{suffix}) and tracks it for cleanup.
    /// </summary>
    public QueueClient CreateQueueClient(string baseName)
    {
        var queueName = $"{baseName}-{_suffix}";
        var client = new QueueClient(ConnectionString, queueName,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        client.CreateIfNotExists();
        _trackedQueues.Add(client);
        return client;
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string GenerateSuffix()
    {
        return Guid.NewGuid().ToString("N")[..8];
    }
}
