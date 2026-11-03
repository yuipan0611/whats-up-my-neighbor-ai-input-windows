#!/usr/bin/env python3
"""Execute an isolated macOS console probe, not a Windows/WPF application."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from verify_bundle import Bundle


def prove(repo, output, dotnet):
    if sys.platform != 'darwin':
        raise RuntimeError('This proof is specifically for the available Mac host; Windows remains separate.')
    repo, output, dotnet = Path(repo).resolve(), Path(output).resolve(), Path(dotnet).resolve()
    if not output.is_relative_to(repo / 'build') or output.exists():
        raise ValueError('Use a new output directory beneath this checkout/build.')
    output.mkdir(parents=True)
    project = repo / 'windows-lite/tools/ContentProbe/ContentProbe.csproj'
    env = dict(os.environ, TMPDIR=str(repo / 'build/tmp'), DOTNET_ROOT=str(dotnet.parent))
    expected = {name: hashlib.sha256(source.read_bytes()).hexdigest() for name, source in {
        'Assets/original.png': repo / 'windows-lite/NekoChat.Windows/Assets/original.png',
        'Coats/original.json': repo / 'Examples/CatAppearance/original.json'}.items()}
    runs = []
    for mode in (False, True):
        stage = output / ('all' if mode else 'native-only')
        published, isolated, cache = stage / 'published', stage / 'isolated', stage / 'extraction'
        isolated.mkdir(parents=True)
        args = [str(dotnet), 'publish', str(project), '-c', 'Release', '-r', 'osx-arm64', '--self-contained', 'false',
                '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                '-p:IncludeAllContentForSelfExtract=' + str(mode).lower(),
                '-p:PublishDir=' + os.path.relpath(published, project.parent) + '/']
        with (stage / 'publish.log').open('w') as log:
            subprocess.run(args, cwd=repo, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        executable = isolated / 'ContentProbe'
        shutil.copy2(published / executable.name, executable)
        if sorted(p.name for p in isolated.iterdir()) != [executable.name]:
            raise AssertionError('Probe must run from executable alone.')
        bundle = Bundle.read(executable)
        if bundle.flags != int(mode):
            raise AssertionError('Actual bundle flag differs from requested mode.')
        run_env = dict(env, DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(cache))
        process = subprocess.run([str(executable)], cwd=isolated, env=run_env, text=True, capture_output=True, timeout=30)
        (stage / 'process.stdout').write_text(process.stdout, encoding='utf-8')
        (stage / 'process.stderr').write_text(process.stderr, encoding='utf-8')
        result = json.loads(process.stdout)
        if mode:
            if process.returncode != 0 or not result['passed']:
                raise AssertionError('All-content process failed.')
            if {f['path']: f['sha256'] for f in result['files']} != expected:
                raise AssertionError('Process loaded changed content.')
            if not Path(result['baseDirectory']).resolve().is_relative_to(cache):
                raise AssertionError('AppContext did not point into the controlled extraction cache.')
        elif process.returncode != 86 or result['passed'] or result['error'] != 'DirectoryNotFoundException':
            raise AssertionError('Native-only negative control did not fail on missing content.')
        runs.append(dict(allContent=mode, exitCode=process.returncode, bundle=bundle.summary(), process=result))
    report = dict(passed=True, macConsoleOnly=True, windowsExecuted=False, runs=runs, expectedContent=expected)
    (output / 'proof.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prove(args.repo, args.output, args.dotnet), ensure_ascii=False, indent=2))
