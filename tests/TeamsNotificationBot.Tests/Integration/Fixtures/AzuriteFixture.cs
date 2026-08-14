using System.Diagnostics;
using System.Net.Sockets;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Fixtures;

/// <summary>
/// xUnit collection fixture that manages an Azurite instance for integration tests.
///
/// The fixture always starts and owns its <b>own</b> Azurite process, on a dedicated port
/// triple and in a per-run data directory that is deleted on dispose. It deliberately does
/// <b>not</b> reuse an Azurite already listening on the default 10000-10002 ports.
///
/// That reuse used to be the behaviour, and it coupled the suite to a process it did not own:
/// `docs/local-development.md` tells developers to run `azurite --location /tmp/azurite` for
/// `func host start`, so the normal local setup had the tests adopt a long-lived instance with
/// unrelated accumulated state. Worse, the fixed data directory meant two concurrent
/// `dotnet test` runs (two terminals, or a repo plus a git worktree) would share one Azurite —
/// whichever run started it would kill it in <see cref="DisposeAsync"/> while the other was
/// still using it, surfacing as unrelated assertion failures like "the collection was empty".
///
/// Owning the process removes that whole class of interference. Ports are chosen from a
/// per-run offset so concurrent runs on one machine do not collide either.
///
/// Tables and queues are additionally isolated via a random suffix (shared per collection
/// fixture instance). Different test classes use distinct base names for their resources.
/// </summary>
public class AzuriteFixture : IAsyncLifetime
{
    // Dedicated port range, deliberately clear of Azurite's 10000-10002 defaults so a
    // developer's `func host start` emulator is never touched by the test suite.
    private const int BasePort = 11000;

    private readonly int _blobPort;
    private readonly int _queuePort;
    private readonly int _tablePort;
    private readonly string _connectionString;
    private readonly string _dataDir;

    private Process? _azuriteProcess;

    private readonly List<TableClient> _trackedTables = [];
    private readonly List<QueueClient> _trackedQueues = [];
    private readonly string _suffix = GenerateSuffix();

    public string Suffix => _suffix;

    public AzuriteFixture()
    {
        // Pick a free port triple so concurrent test runs on one machine don't collide.
        var offset = FindFreePortTriple();
        _blobPort = BasePort + offset;
        _queuePort = BasePort + offset + 1;
        _tablePort = BasePort + offset + 2;

        _connectionString =
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            $"BlobEndpoint=http://127.0.0.1:{_blobPort}/devstoreaccount1;" +
            $"QueueEndpoint=http://127.0.0.1:{_queuePort}/devstoreaccount1;" +
            $"TableEndpoint=http://127.0.0.1:{_tablePort}/devstoreaccount1";

        _dataDir = Path.Combine(Path.GetTempPath(), $"azurite-tests-{_suffix}");
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);

        _azuriteProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "azurite",
                Arguments = $"--silent --skipApiVersionCheck --location {_dataDir} " +
                            $"--blobPort {_blobPort} --queuePort {_queuePort} --tablePort {_tablePort}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _azuriteProcess.Start();

        // Wait for table and queue services to become ready
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (_azuriteProcess.HasExited)
            {
                var stderr = await _azuriteProcess.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"Azurite exited with code {_azuriteProcess.ExitCode} during startup. " +
                    $"Is it installed (`npm install -g azurite`)? stderr: {stderr}");
            }

            if (IsPortOpen(_tablePort) && IsPortOpen(_queuePort))
                return;

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Azurite did not become ready on ports {_blobPort}/{_queuePort}/{_tablePort} within 30 seconds");
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

        // We always own the process, so we always tear it down.
        if (_azuriteProcess is { HasExited: false })
        {
            _azuriteProcess.Kill(entireProcessTree: true);
            await _azuriteProcess.WaitForExitAsync();
        }
        _azuriteProcess?.Dispose();

        // Per-run data directory — nothing should survive to bleed into the next run.
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Creates a table with an isolated name ({baseName}{suffix}) and tracks it for cleanup.
    /// </summary>
    public TableClient CreateTableClient(string baseName)
    {
        var tableName = $"{baseName}{_suffix}";
        var client = new TableClient(_connectionString, tableName);
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
        var client = new QueueClient(_connectionString, queueName,
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

    /// <summary>
    /// Finds an offset from <see cref="BasePort"/> where three consecutive ports are free, so
    /// concurrent test runs on the same machine each get their own Azurite. Steps by 10 to keep
    /// the triples clearly separated.
    /// </summary>
    private static int FindFreePortTriple()
    {
        for (var offset = 0; offset <= 200; offset += 10)
        {
            if (!IsPortOpen(BasePort + offset) &&
                !IsPortOpen(BasePort + offset + 1) &&
                !IsPortOpen(BasePort + offset + 2))
            {
                return offset;
            }
        }

        throw new InvalidOperationException(
            $"No free Azurite port triple found in {BasePort}-{BasePort + 202}.");
    }
}
