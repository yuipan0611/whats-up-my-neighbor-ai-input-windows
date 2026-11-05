#!/usr/bin/env python3
"""Build and verify local Windows candidates. Never installs, signs, publishes or executes the generated Windows App/Setup candidates."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import zipfile
from verify_bundle import Bundle, verify_portable, required_content

DOCS = ['README.md', 'DELIVERY.md', 'BACKUP-RESTORE.md', 'SEND-CANCELLATION.md', 'THIRD-PARTY.md', 'ATTACHMENTS.md']


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def build(repo, output, dotnet):
    repo, output = Path(repo).resolve(), Path(output).resolve()
    dotnet = shutil.which(dotnet) or str(Path(dotnet).resolve())
    if not output.is_relative_to(repo / 'build') or output.exists():
        raise ValueError('Use a new output directory beneath checkout/build; existing artifacts are preserved.')
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=repo, text=True).strip()
    if git('status', '--porcelain'):
        raise RuntimeError('Freeze and commit the source tree first.')
    commit = git('rev-parse', 'HEAD')
    required_content(repo)  # Reject ignored/untracked data under publish globs before compiling.
    output.mkdir(parents=True)
    logs = output / 'logs'
    logs.mkdir()
    temporary = repo / 'build/tmp'
    temporary.mkdir(exist_ok=True)
    env = dict(os.environ, TMPDIR=str(temporary))
    env.setdefault('NUGET_PACKAGES', str(repo / 'build/nuget-cache'))
    commands = []
    def run(name, args):
        commands.append(args)
        with (logs / (name + '.log')).open('w') as log:
            subprocess.run(args, cwd=repo, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        print(name + ' passed', flush=True)
    def sdk(name, *args):
        run(name, [dotnet, *args])
    def publish_arg(folder, project):
        # Relative properties also avoid apostrophes breaking MSBuild item transforms.
        return '-p:PublishDir=' + os.path.relpath(folder, (repo / project).parent) + '/'
    def archive(folder, target):
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as packed:
            for file in sorted(folder.rglob('*')):
                if file.is_file():
                    packed.write(file, file.relative_to(folder))
        with zipfile.ZipFile(target) as packed:
            if packed.testzip() is not None:
                raise AssertionError('ZIP CRC failure')
    def pe_x64(path):
        with path.open('rb') as file:
            if file.read(2) != b'MZ':
                raise AssertionError('Missing PE host')
            file.seek(0x3c)
            offset = struct.unpack('<I', file.read(4))[0]
            file.seek(offset)
            if file.read(4) != b'PE\0\0' or struct.unpack('<H', file.read(2))[0] != 0x8664:
                raise AssertionError('Expected AMD64 host')
    core, animation = 'windows-lite/NekoChat.Tests', 'windows-lite/NekoChat.Animation.Tests'
    sdk('core-restore', 'restore', core, '--locked-mode')
    sdk('core', 'run', '--project', core, '-c', 'Release', '--no-restore')
    sdk('classroom-all', 'run', '--project', core, '-c', 'Release', '--no-build', '--', '--classroom-all')
    sdk('animation-restore', 'restore', animation, '--locked-mode')
    sdk('animation', 'run', '--project', animation, '-c', 'Release', '--no-restore', '--', 'windows-lite/NekoChat.Animation/Assets/manifest.json')
    app = 'windows-lite/NekoChat.Windows/NekoChat.Windows.csproj'
    full, portable, setup = (output / name for name in ['full', 'portable', 'setup'])
    sdk('full-restore', 'restore', app, '-r', 'win-x64', '--locked-mode')
    sdk('full', 'publish', app, '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', publish_arg(full, app))
    for name in DOCS:
        shutil.copyfile(repo / 'windows-lite' / name, full / name)
    shutil.copyfile(repo / 'LICENSE', full / 'LICENSE')
    (full / 'tools').mkdir()
    shutil.copyfile(repo / 'windows-lite/tools/README.md', full / 'tools/README.md')
    sdk('portable-restore', 'restore', app, '-r', 'win-x64', '-p:PublishSingleFile=true', '--locked-mode')
    sdk('portable', 'publish', app, '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', publish_arg(portable, app))
    standalone = output / 'NekoChat-Lite.exe'
    shutil.copyfile(portable / 'NekoChat.Windows.exe', standalone)
    proof = verify_portable(standalone, repo)
    (output / 'portable-bundle.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    full_zip = output / 'NekoChat-Windows-Lite-win-x64.zip'
    archive(full, full_zip)
    checksum = output / 'payload.sha256'
    checksum.write_text(digest(full_zip) + '\n', encoding='ascii')
    installer = 'windows-lite/NekoChat.Installer/NekoChat.Installer.csproj'
    sdk('installer-restore', 'restore', installer, '-r', 'win-x64')
    sdk('installer', 'publish', installer, '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PackagePayload=' + os.path.relpath(full_zip, (repo / installer).parent),
        '-p:PackageChecksum=' + os.path.relpath(checksum, (repo / installer).parent), publish_arg(setup, installer))
    setup_exe = output / 'NekoChat-Setup-win-x64.exe'
    shutil.copyfile(setup / setup_exe.name, setup_exe)
    for file in [standalone, setup_exe, full / 'NekoChat.Windows.exe']:
        pe_x64(file)
    # Read the installed assembly from the actual bundle, never through the Windows loader.
    bundled_setup = Bundle.read(setup_exe)
    managed = output / '.installer-metadata.dll'
    managed.write_bytes(bundled_setup.content('NekoChat-Setup-win-x64.dll'))
    verifier = 'windows-lite/tools/PackageVerifier/PackageVerifier.csproj'
    try:
        sdk('metadata-verifier', 'run', '--project', verifier, '--', str(managed), str(setup_exe), str(full_zip), str(standalone), str(repo / 'windows-lite/NekoChat.Windows/bin/Release/net10.0-windows/win-x64/NekoChat.Windows.dll'))
        verified = json.loads((logs / 'metadata-verifier.log').read_text(encoding='utf-8-sig').splitlines()[-1])
        if not verified['passed'] or verified['windowsExecuted']:
            raise AssertionError('Offline resource verification failed')
    finally:
        managed.unlink(missing_ok=True)
    counts = {}
    for name, expression in [('core', r'NekoChat core: (\d+) behavior checks passed'), ('animation', r'Animation preview: (\d+) checks passed')]:
        matched = re.search(expression, (logs / (name + '.log')).read_text(encoding='utf-8-sig'))
        if matched is None:
            raise AssertionError('Missing completed suite result: ' + name)
        counts[name] = int(matched[1])
    assets = []
    for file in [full_zip, standalone, setup_exe]:
        assets.append(dict(name=file.name, bytes=file.stat().st_size, sha256=digest(file)))
        (output / (file.name + '.sha256')).write_text(assets[-1]['sha256'] + '  ' + file.name + '\n', encoding='ascii')
    if git('status', '--porcelain') or git('rev-parse', 'HEAD') != commit:
        raise AssertionError('Source changed during packaging')
    manifest = dict(sourceCommit=commit, sourceBranch=git('branch', '--show-current'),
        sdk=subprocess.check_output([dotnet, '--version'], env=env, text=True).strip(), target='win-x64',
        checks=counts, portableStandalone=True, allContentFlag=proof['flags'], windowsExecuted=False,
        signedOrSignatureVerified=False, installed=False, released=False, policySelectedByBuilder=False,
        assets=assets, fullPayloadSha256=digest(full_zip),
        fullFiles=[dict(path=p.relative_to(full).as_posix(), bytes=p.stat().st_size, sha256=digest(p)) for p in sorted(full.rglob('*')) if p.is_file()],
        commands=commands, logHashes={p.name: digest(p) for p in sorted(logs.glob('*.log'))})
    (output / 'delivery-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    shutil.copyfile(repo / 'windows-lite/DELIVERY.md', output / 'DELIVERY.md')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    options = parser.parse_args()
    print(json.dumps(build(options.repo, options.output, options.dotnet), ensure_ascii=False, indent=2))
