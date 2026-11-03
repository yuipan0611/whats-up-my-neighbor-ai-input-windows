namespace NekoChat.Tests;

/// <summary>Preserved signed-V2/P1 flows superseded by PMCM R3/R5. Shared guards remain in the default group (M1).</summary>
internal static class ArchivedSupersededChecks
{
    private static readonly HashSet<string> FocusedArguments = new(StringComparer.Ordinal)
    {
        "--receipt-archived",
        "--v2-vectors",
        "--manual-send-cancel",
        "--v2-tcp",
        "--v2-tcp-crashes",
        "--shared-port",
        "--passive-identity",
        "--pairing-host",
        "--pairing-roster",
        "--pairing-mitm",
        "--pairing-crashes",
        "--pairing-cross",
        "--pairing-tcp",
        "--pairing-actors",
        "--pairing-responder",
        "--pairing-primitives",
        "--identity-recovery",
        "--identity-vault",
        "--presence-restart-drain",
        "--v2-store-crashes",
    };
    internal static bool IsFocusedArgument(string argument) => FocusedArguments.Contains(argument);
    internal static async Task<int> Run()
    {
        Console.WriteLine("archived-superseded: historical signed-V2/P1 regression; historical intermittent P1 repair failure remains UNRESOLVED.");
        int checks = 0;
        checks += await V2VectorChecks.Run();
        checks += await V2TcpChecks.Run();
        checks += await V2TcpChecks.RunCancellation();
        checks += await V2CrashChecks.Run();
        checks += await V2CrashMoreChecks.Run();
        checks += await SharedPortChecks.Run();
        checks += await PassiveIdentityChecks.Run();
        checks += await IdentityVaultChecks.Run();
        checks += await IdentityRecoveryChecks.Run();
        checks += PairingWireChecks.Run();
        checks += await PairingCandidateChecks.Run();
        checks += await PairingResponderChecks.Run();
        checks += await PairingInitiatorChecks.Run();
        checks += await PairingTcpChecks.Run();
        checks += await PairingCrossChecks.Run();
        checks += await PairingCrashChecks.Run();
        checks += await PairingCrossChecks.RunMitm();
        checks += await PairingRosterChecks.Run();
        checks += await PairingRosterChecks.RunHost();
        checks += await ReceiptServiceChecks.RunArchivedProtocol();
        Console.WriteLine($"archived-superseded: {checks} historical checks passed; not current classroom acceptance or evidence the historical intermittent repair fault is fixed.");
        return checks;
    }
}
