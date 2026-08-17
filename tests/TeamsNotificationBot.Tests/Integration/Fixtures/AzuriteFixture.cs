using System.ComponentModel;
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

    // Number of candidate port triples, spaced 10 apart, starting at BasePort.
    private const int SlotCount = 20;
    private const int StartAttempts = 8;

    private int _blobPort;
    private int _queuePort;
    private int _tablePort;
    private string _connectionString = "";
    private readonly string _dataDir;

    private Process? _azuriteProcess;

    private readonly List<TableClient> _trackedTables = [];
    private readonly List<QueueClient> _trackedQueues = [];
    private readonly string _suffix = GenerateSuffix();

    public string Suffix => _suffix;

    public AzuriteFixture()
    {
        // Path.Join rather than Path.Combine: Join always concatenates, where Combine would
        // silently discard the temp path if the second segment were ever rooted.
        _dataDir = Path.Join(Path.GetTempPath(), $"azurite-tests-{_suffix}");
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);

        // Probing for a free port and then binding it is inherently racy: two runs starting at
        // the same moment would both see the same triple free and both try to claim it. So start
        // from a random slot to spread simultaneous starts out, and treat a failed bind as a
        // normal outcome to retry on rather than an error.
        var slot = Random.Shared.Next(SlotCount);
        var failures = new List<string>();

        for (var attempt = 0; attempt < StartAttempts; attempt++)
        {
            var offset = ((slot + attempt) % SlotCount) * 10;
            _blobPort = BasePort + offset;
            _queuePort = BasePort + offset + 1;
            _tablePort = BasePort + offset + 2;

            if (IsPortOpen(_blobPort) || IsPortOpen(_queuePort) || IsPortOpen(_tablePort))
                continue; // cheap pre-check; the bind below is the real arbiter

            var started = await TryStartAzuriteAsync();
            if (started is null)
            {
                _connectionString =
                    "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                    "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
                    $"BlobEndpoint=http://127.0.0.1:{_blobPort}/devstoreaccount1;" +
                    $"QueueEndpoint=http://127.0.0.1:{_queuePort}/devstoreaccount1;" +
                    $"TableEndpoint=http://127.0.0.1:{_tablePort}/devstoreaccount1";
                return;
            }

            failures.Add($"{_blobPort}/{_queuePort}/{_tablePort}: {started}");
        }

        throw new InvalidOperationException(
            $"Could not start Azurite after {StartAttempts} attempts. Is it installed " +
            $"(`npm install -g azurite`)?\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Starts Azurite on the currently selected ports. Returns null on success, or a short
    /// description of why this attempt failed (port already taken, crashed, never got ready).
    /// </summary>
    private async Task<string?> TryStartAzuriteAsync()
    {
        var process = new Process
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

        // Process.Start documents exactly these two for a launch failure: Win32Exception when the
        // executable can't be opened (azurite not on PATH), InvalidOperationException for bad start
        // configuration. Anything else is a genuine defect and should surface, not be swallowed.
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return LaunchFailed(process, ex);
        }
        catch (InvalidOperationException ex)
        {
            return LaunchFailed(process, ex);
        }

        static string LaunchFailed(Process p, Exception ex)
        {
            p.Dispose();
            return $"could not launch azurite ({ex.Message})";
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (process.HasExited)
            {
                // Typically EADDRINUSE — another run claimed this triple between our check and
                // our bind. Caller retries on the next slot.
                var stderr = await process.StandardError.ReadToEndAsync();
                var reason = $"exited with code {process.ExitCode}" +
                             (string.IsNullOrWhiteSpace(stderr) ? "" : $" ({stderr.Trim()})");
                process.Dispose();
                return reason;
            }

            if (IsPortOpen(_tablePort) && IsPortOpen(_queuePort))
            {
                _azuriteProcess = process;
                return null;
            }

            await Task.Delay(200);
        }

        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
        process.Dispose();
        return "did not become ready within 30 seconds";
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

        // Per-run data directory — nothing should survive to bleed into the next run. Best-effort:
        // a leftover temp directory is not worth failing a green test run over, but only the
        // filesystem errors that deletion can legitimately hit are swallowed.
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException) { /* already gone */ }
        catch (IOException) { /* file still locked by a draining Azurite handle */ }
        catch (UnauthorizedAccessException) { /* read-only or permission-denied entry */ }
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

}
