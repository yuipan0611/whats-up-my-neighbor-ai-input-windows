#!/usr/bin/env python3
"""Read .NET6+ bundle metadata without loading or executing its assemblies.
Format authority: dotnet/runtime v10.0.14 apphost/bundle_marker.cpp,
bundle/header.h and installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import zlib

SIGNATURE = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
MAX_BYTES = 512 * 1024 * 1024


class BundleError(ValueError):
    pass


class Reader:
    def __init__(self, data, offset):
        self.data, self.position = data, offset

    def take(self, size):
        if size < 0 or self.position + size > len(self.data):
            raise BundleError('truncated metadata')
        start = self.position
        self.position += size
        return self.data[start:self.position]

    def number(self, fmt):
        return struct.unpack(fmt, self.take(struct.calcsize(fmt)))[0]

    def string(self, maximum):
        length = 0
        for index in range(5):
            value = self.number('<B')
            if index == 4 and value > 7:
                raise BundleError('invalid string length')
            length |= (value & 127) << (index * 7)
            if value < 128:
                if not 1 <= length <= maximum:
                    raise BundleError('string limit')
                try:
                    return self.take(length).decode('utf-8', errors='strict')
                except UnicodeDecodeError as error:
                    raise BundleError('invalid UTF8') from error
        raise BundleError('invalid string prefix')


class Bundle:
    def __init__(self, data):
        if not 40 <= len(data) <= MAX_BYTES:
            raise BundleError('bundle size limit')
        candidates, start = [], 0
        for _ in range(64):
            marker = data.find(SIGNATURE, start)
            if marker < 0:
                break
            start = marker + len(SIGNATURE)
            if marker < 8:
                continue
            candidate = object.__new__(Bundle)
            try:
                candidate._parse(data, marker)
            except (BundleError, struct.error):
                continue
            candidates.append(candidate)
        else:
            raise BundleError('bundle marker limit')
        if len(candidates) != 1:
            raise BundleError('missing/ambiguous valid bundle metadata')
        self.__dict__.update(candidates[0].__dict__)

    def _parse(self, data, marker):
        header = struct.unpack_from('<q', data, marker - 8)[0]
        if not marker + len(SIGNATURE) <= header < len(data) - 12:
            raise BundleError('invalid header offset')
        reader = Reader(data, header)
        major, minor, count = reader.number('<I'), reader.number('<I'), reader.number('<i')
        if (major, minor) != (6, 0) or not 1 <= count <= 4096:
            raise BundleError('unsupported version/file count')
        self.identifier = reader.string(128)
        if any(not (c.isascii() and (c.isalnum() or c in '_-+=')) for c in self.identifier):
            raise BundleError('invalid bundle id')
        cached = [reader.number('<q') for _ in range(4)]
        self.flags = reader.number('<Q')
        if self.flags & ~1:
            raise BundleError('unsupported bundle flags')
        self.entries, folded, ranges = {}, set(), []
        for _ in range(count):
            offset, size, compressed = (reader.number('<q') for _ in range(3))
            kind, name = reader.number('<B'), reader.string(1024)
            if (name.startswith('/') or '\\' in name or any(c in name for c in ':<>"|?*') or
                    any(ord(c) < 32 for c in name) or any(p in ('', '.', '..') for p in name.split('/'))):
                raise BundleError('noncanonical path')
            if name.casefold() in folded:
                raise BundleError('duplicate path')
            folded.add(name.casefold())
            length = compressed or size
            if kind > 5 or offset < 40 or size < 0 or compressed < 0 or size > MAX_BYTES or offset + length > header:
                raise BundleError('invalid file range/type')
            if compressed and compressed >= size:
                raise BundleError('invalid compressed range')
            self.entries[name] = dict(offset=offset, size=size, compressed=compressed, kind=kind)
            if length:
                ranges.append((offset, offset + length))
        ranges.sort()
        if any(left[1] > right[0] for left, right in zip(ranges, ranges[1:])):
            raise BundleError('overlapping file ranges')
        for offset, size in zip(cached[::2], cached[1::2]):
            if (offset == 0) != (size == 0) or offset < 0 or size < 0 or offset + size > header:
                raise BundleError('invalid cached JSON range')
            if offset and not any(e['offset'] == offset and e['size'] == size for e in self.entries.values()):
                raise BundleError('cached JSON not in manifest')
        self.data, self.header, self.version = data, header, [major, minor]

    @classmethod
    def read(cls, path):
        path = Path(path)
        if path.stat().st_size > MAX_BYTES:
            raise BundleError('bundle size limit')
        return cls(path.read_bytes())

    def content(self, name):
        entry = self.entries[name]
        raw = self.data[entry['offset']:entry['offset'] + (entry['compressed'] or entry['size'])]
        if entry['compressed']:
            inflater = zlib.decompressobj(-15)
            try:
                raw = inflater.decompress(raw, entry['size'] + 1)
            except zlib.error as error:
                raise BundleError('invalid compressed content') from error
            if len(raw) != entry['size'] or not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
                raise BundleError('invalid compressed content')
        return raw

    def summary(self):
        return dict(version=self.version, flags=self.flags, fileCount=len(self.entries), headerOffset=self.header)


def required_content(repo):
    repo = Path(repo)
    result = {}
    asset_root = repo / 'windows-lite/NekoChat.Windows'
    content_roots = [asset_root / 'Assets', repo / 'Examples/CatAppearance']
    paths = content_roots + list(content_roots[0].rglob('*')) + list(content_roots[1].glob('*.json'))
    if any(p.is_symlink() or getattr(p, 'is_junction', lambda: False)() for p in paths):
        raise BundleError('linked publish input')
    inputs = [p for p in content_roots[0].rglob('*') if p.is_file()]
    inputs += list(content_roots[1].glob('*.json'))
    tracked = set(subprocess.check_output(['git', 'ls-files', '-z', '--', 'windows-lite/NekoChat.Windows/Assets', 'Examples/CatAppearance'], cwd=repo).decode().split('\0'))
    if any(p.is_symlink() or p.relative_to(repo).as_posix() not in tracked for p in inputs):
        raise BundleError('untracked or linked publish input')
    for source in (asset_root / 'Assets').rglob('*'):
        if source.is_file() and not (source.parent.name == 'Reactions' and source.suffix == '.png'):
            result[source.relative_to(asset_root).as_posix()] = source
    for source in (repo / 'Examples/CatAppearance').glob('*.json'):
        result['Coats/' + source.name] = source
    result['THIRD-PARTY-LICENSES.txt'] = repo / 'windows-lite/THIRD-PARTY-LICENSES.txt'
    if 'Assets/original.png' not in result or 'Coats/original.json' not in result:
        raise BundleError('incomplete source tree')
    return result


def verify_portable(executable, repo):
    executable = Path(executable)
    bundle = Bundle.read(executable)
    if bundle.flags != 1:
        raise BundleError('all-content compatibility mode is required')
    # Inspect the PE apphost only; no Windows loader or managed reflection is invoked.
    if bundle.data[:2] != b'MZ':
        raise BundleError('not a PE apphost')
    pe = struct.unpack_from('<I', bundle.data, 0x3c)[0]
    if pe + 6 > len(bundle.data) or bundle.data[pe:pe + 4] != b'PE\0\0' or struct.unpack_from('<H', bundle.data, pe + 4)[0] != 0x8664:
        raise BundleError('not an AMD64 apphost')
    for name in ['NekoChat.Windows.dll', 'NekoChat.Core.dll', 'NekoChat.Animation.dll', 'NekoChat.Windows.deps.json', 'NekoChat.Windows.runtimeconfig.json']:
        if name not in bundle.entries:
            raise BundleError('missing required managed runtime input: ' + name)
    proofs = []
    for name, source in sorted(required_content(repo).items()):
        if name not in bundle.entries or bundle.content(name) != source.read_bytes():
            raise BundleError('missing/changed bundled content: ' + name)
        proofs.append(dict(path=name, sha256=hashlib.sha256(bundle.content(name)).hexdigest(), bytes=source.stat().st_size))
    return dict(passed=True, executable=executable.name, sha256=hashlib.sha256(bundle.data).hexdigest(),
                **bundle.summary(), content=proofs, windowsExecuted=False,
                scope='PE/bundle/content proof only; Windows WPF/extraction/permissions remain unexecuted')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        result = verify_portable(args.executable, args.repo)
    except (BundleError, OSError, struct.error) as error:
        result = dict(passed=False, error=str(error), windowsExecuted=False)
    rendered = json.dumps(result, ensure_ascii=False, indent=2) + '\n'
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding='utf-8')
    print(rendered)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
