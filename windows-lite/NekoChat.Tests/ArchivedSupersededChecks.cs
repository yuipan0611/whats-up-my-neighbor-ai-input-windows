namespace NekoChat.Tests;

/// <summary>Preserved signed-V2/P1 regressions superseded by PMCM R3/R5. Never included in the current classroom count.</summary>
internal static class ArchivedSupersededChecks
{
    private static readonly HashSet<string> FocusedArguments = new(StringComparer.Ordinal)
    {
        "--presence-aba-probe",
        "--v2-vectors",
        "--v2-boundaries",
        "--v2-storage-fences",
        "--manual-send-cancel",
        "--v2-tcp",
        "--v2-tcp-crashes",
        "--shared-port",
        "--presence-wire",
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
        "--receipt-worker",
    };
    internal static bool IsFocusedArgument(string argument) => FocusedArguments.Contains(argument);
    internal static async Task<int> Run()
    {
        Console.WriteLine("archived-superseded: historical signed-V2/P1 regression; historical intermittent P1 repair failure remains UNRESOLVED.");
        int checks = 0;
        checks += await PresenceChecks.Run();
        checks += PresenceChecks.RunAbaProbe();
        checks += await SecurityRevisionChecks.Run();
        checks += await V2VectorChecks.Run();
        checks += await V2BoundaryChecks.Run();
        checks += V2StorageFenceChecks.Run();
        checks += await V2TcpChecks.Run();
        checks += await V2TcpChecks.RunCancellation();
        checks += await V2CrashChecks.Run();
        checks += await V2CrashMoreChecks.Run();
        checks += await SharedPortChecks.Run();
        checks += await PresenceWireChecks.Run();
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
        checks += await ReceiptServiceChecks.Run();
        Console.WriteLine($"archived-superseded: {checks} historical checks passed; not current classroom acceptance or evidence the historical intermittent repair fault is fixed.");
        return checks;
    }
}
