using System.Text.Json;
using UltimateRemoteAgent.Enrollment;
using UltimateRemoteAgent.Local;
using UltimateRemoteAgent.Runtime;

namespace UltimateRemoteAgent;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            using InteractiveUserInstanceLock instanceLock = InteractiveUserInstanceLock.Acquire();
            return args switch
            {
                ["run"] => await RunAgentAsync().ConfigureAwait(false),
                ["pair", string origin, string macroRoot] =>
                    await PairAsync(origin, macroRoot).ConfigureAwait(false),
                ["inspect", string macroRoot] =>
                    await InspectAsync(macroRoot).ConfigureAwait(false),
                _ => PrintUsage(),
            };
        }
        catch (AgentRuntimeException exception)
        {
            SafeLog.Error(exception.Code);
            return 2;
        }
        catch (EnrollmentException exception)
        {
            SafeLog.Error(exception.Code);
            return 3;
        }
        catch (PairingClientException exception)
        {
            SafeLog.Error(exception.Code);
            return 4;
        }
        catch (StrategyCatalogException exception)
        {
            SafeLog.Error($"STRATEGY_CATALOG_{exception.Error.ToString().ToUpperInvariant()}");
            return 5;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception)
        {
            SafeLog.Error("UNEXPECTED_FAILURE");
            return 10;
        }
    }

    private static async Task<int> PairAsync(string originText, string macroRootText)
    {
        if (!Uri.TryCreate(originText, UriKind.Absolute, out Uri? origin))
        {
            throw new EnrollmentException("HTTPS_ORIGIN_INVALID");
        }

        Uri validatedOrigin = EnrollmentValidator.ValidateOrigin(origin);
        string macroRoot = EnrollmentValidator.ValidateMacroRoot(macroRootText, requireFiles: true);

        // Validating the approved root before ticket redemption prevents binding a
        // credential to an installation whose catalog escapes through a reparse point.
        _ = StrategyCatalog.Load(macroRoot);
        // Validate the fixed executable and Remote entrypoint through final handles too.
        // A one-shot pairing ticket must not be consumed for an installation that the
        // exact-process census would later refuse to inspect.
        _ = new WmiMacroProcessCensus(macroRoot);
        string ticket = SecretConsole.ReadPairingTicket();
        PairingResult result;
        try
        {
            using var client = new PairingClient();
            result = await client.RedeemAsync(validatedOrigin, ticket, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            ticket = string.Empty;
        }

        var enrollment = new EnrollmentRecord(
            EnrollmentRecord.CurrentVersion,
            validatedOrigin,
            result.WebSocketUri,
            macroRoot,
            result.DeviceCredential);
        var store = new DpapiEnrollmentStore(DpapiEnrollmentStore.DefaultPath);
        store.Save(enrollment);
        SafeLog.Info("PAIRING_COMPLETE");
        return 0;
    }

    private static async Task<int> RunAgentAsync()
    {
        var store = new DpapiEnrollmentStore(DpapiEnrollmentStore.DefaultPath);
        EnrollmentRecord enrollment = store.Load();
        using var bridge = new ReadOnlyLocalBridge(enrollment.MacroRoot);
        var host = new AgentHost(enrollment, bridge);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            SafeLog.Info("AGENT_STARTING");
            await host.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> InspectAsync(string macroRootText)
    {
        string macroRoot = EnrollmentValidator.ValidateMacroRoot(macroRootText, requireFiles: true);
        using var bridge = new ReadOnlyLocalBridge(macroRoot);
        Protocol.MacroSnapshot snapshot = await bridge.GetSnapshotAsync(CancellationToken.None)
            .ConfigureAwait(false);
        IReadOnlyList<Protocol.StrategySummary> strategies =
            await bridge.ListStrategiesAsync(CancellationToken.None).ConfigureAwait(false);

        string json = JsonSerializer.Serialize(new
        {
            protocol = 1,
            snapshot = new
            {
                macro_state = snapshot.MacroState switch
                {
                    Protocol.MacroState.NotRunning => "not_running",
                    Protocol.MacroState.Idle => "idle",
                    Protocol.MacroState.Running => "running",
                    _ => "unknown",
                },
                roblox_running = snapshot.RobloxRunning,
                current_strategy_id = snapshot.CurrentStrategyId,
            },
            strategies = strategies.Select(strategy => new
            {
                strategy_id = strategy.StrategyId,
                name = strategy.Name,
            }),
        });
        Console.Out.WriteLine(json);
        return 0;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("UltimateRemoteAgent 0.3.0");
        Console.Error.WriteLine("  UltimateRemoteAgent.exe pair <https-origin> <macro-root>");
        Console.Error.WriteLine("  UltimateRemoteAgent.exe run");
        Console.Error.WriteLine("  UltimateRemoteAgent.exe inspect <macro-root>");
        return 1;
    }
}
