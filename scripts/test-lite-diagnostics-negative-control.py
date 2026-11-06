#!/usr/bin/env python3
"""Prove diagnostics regressions against product mutations in a private HEAD archive.

Run after committing the candidate. Original product files and test assertions are never edited.
Only the synthetic archive uses loopback/SQLite. Set NUGET_PACKAGES to the existing cache.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", default="build/diagnostics-negative-control")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    output = (repo / args.output).resolve()
    if not output.is_relative_to(repo / "build"):
        parser.error("output must be inside this worktree's build directory")
    output.mkdir(parents=True, exist_ok=True)
    candidate = output / ("candidate-" + uuid.uuid4().hex)
    candidate.mkdir()
    paths = ["Shared", "Examples/CatAppearance", "windows-lite/NekoChat.Core",
             "windows-lite/NekoChat.Tests", "windows-lite/NekoChat.Animation",
             "windows-lite/NekoChat.Windows/Assets", "windows-lite/NekoChat.Windows/Identity/IdentityFileVault.cs",
             "windows-lite/NekoChat.Windows/Settings.cs", "windows-lite/NekoChat.Windows/UiTestOptions.cs"]
    source_head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
    archive = subprocess.check_output(["git", "archive", "HEAD", *paths], cwd=repo)
    subprocess.run(["tar", "-xf", "-", "-C", str(candidate)], input=archive, check=True)
    temporary = candidate / "build/tmp"
    temporary.mkdir(parents=True)
    env = dict(os.environ, TMPDIR=str(temporary), DOTNET_CLI_TELEMETRY_OPTOUT="1")
    results = []

    def run(name, arguments, expected_failure=None):
        command = [args.dotnet, *arguments]
        result = subprocess.run(command, cwd=candidate, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        log = output / (name + ".log")
        log.write_bytes(result.stdout)
        text = result.stdout.decode("utf-8", errors="replace")
        verified = result.returncode == 0 if expected_failure is None else (
            result.returncode != 0 and expected_failure in text and "error CS" not in text)
        proof = dict(name=name, exit=result.returncode, expectedFailure=expected_failure,
                     verified=verified, log=log.name, sha256=hashlib.sha256(result.stdout).hexdigest())
        results.append(proof)
        print(json.dumps(proof), flush=True)
        if not verified:
            raise RuntimeError("Unexpected result: " + name + "; inspect " + str(log))

    project = "windows-lite/NekoChat.Tests"
    diagnostic = Path("windows-lite/NekoChat.Core/Diagnostics/DiagnosticJournal.cs")
    policy = Path("windows-lite/NekoChat.Core/Diagnostics/DiagnosticRecoveryPolicy.cs")
    mutations = [
        ("M1-overwrite", policy,
         "=> failures\n            .Where(failure => failure.Stage != stage || failure.Fault.Code != fault.Code)\n            .Append(new(stage, fault)).ToArray();",
         "=> [new(stage, fault)];", "diagnostic-recovery",
         "M1 adding transient failure preserves unresolved DiskFull"),
        ("M2-clear-unrelated", policy, ".Where(failure => !CanRecover(failure, stage, writeConfirmed)).ToArray();",
         ".Where(_ => false).ToArray();", "diagnostic-recovery", "M1 transient recovery removes only the matching failure"),
        ("M3-hide-severe", policy,
         "=> failures\n        .OrderByDescending(failure => failure.Fault.StopDataOperations)\n        .ThenByDescending(NeedsWriteConfirmation)\n        .ThenByDescending(failure => failure.Fault.Severity)\n        .ThenBy(failure => failure.Stage).ThenBy(failure => failure.Fault.Code).FirstOrDefault();",
         "=> failures.LastOrDefault();", "diagnostic-recovery", "persistent storage failure stays visible over transient failure"),
        ("M4-export-starts-writer", diagnostic,
         "new(directory, version, null, readOnly: true);", "new(directory, version, null, readOnly: false);",
         "diagnostic-journal", "read-only archive includes unchanged retained generation"),
        ("P1b-custom-exception-message", diagnostic, '_ => "OtherException"', '_ => error.Message',
         "diagnostic-journal", "unknown custom exception exports fixed OtherException"),
    ]
    originals = {path: (repo / path).read_bytes() for path in (diagnostic, policy)}
    try:
        run("restore", ["restore", project, "--locked-mode"])
        run("original", ["run", "--project", project, "-c", "Release", "--no-restore", "--", "--diagnostics"])
        for name, path, before, after, argument, expected in mutations:
            file = candidate / path
            content = file.read_text()
            if content.count(before) != 1:
                raise RuntimeError("Mutation anchor not unique: " + name)
            try:
                file.write_text(content.replace(before, after, 1))
                run(name, ["run", "--project", project, "-c", "Release", "--no-restore", "--", "--" + argument], expected)
            finally:
                file.write_text(content)
        run("restored", ["run", "--project", project, "-c", "Release", "--no-restore", "--", "--diagnostics"])
    finally:
        unchanged = all((repo / path).read_bytes() == data for path, data in originals.items())
        summary = dict(sourceHead=source_head, originalProductUnchanged=unchanged, results=results,
                       passed=unchanged and len(results) == len(mutations) + 3 and all(item["verified"] for item in results))
        (output / "negative-control.json").write_text(json.dumps(summary, indent=2) + "\n")
    if not summary["passed"]:
        raise RuntimeError("Negative control verification did not pass")


if __name__ == "__main__":
    main()
