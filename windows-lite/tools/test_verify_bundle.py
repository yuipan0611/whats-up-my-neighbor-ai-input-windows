import struct
import unittest
import zlib
from verify_bundle import Bundle, BundleError, SIGNATURE


def fixture(payload=b'abc', names=('Assets/test',), flags=1, compressed=False, base=0):
    # Independent encoding of the documented v6 record layout; expected values below are literal.
    stored = zlib.compress(payload, wbits=-15) if compressed else payload
    header_offset = base + 40 + len(stored)
    header = struct.pack('<IIi', 6, 0, len(names)) + b'\x0cFixedProbeID' + struct.pack('<qqqqQ', 0, 0, 0, 0, flags)
    for name in names:
        path = name.encode()
        header += struct.pack('<qqqB', base + 40, len(payload), len(stored) if compressed else 0, 0) + bytes([len(path)]) + path
    return struct.pack('<q', header_offset) + SIGNATURE + stored + header


class BundleChecks(unittest.TestCase):
    def test_literal_entry(self):
        bundle = Bundle(fixture())
        self.assertEqual(bundle.flags, 1)
        self.assertEqual(bundle.entries['Assets/test'], dict(offset=40, size=3, compressed=0, kind=0))
        self.assertEqual(bundle.content('Assets/test'), b'abc')

    def test_inspects_noncompat_mode_without_mislabeling(self):
        self.assertEqual(Bundle(fixture(flags=0)).flags, 0)

    def test_actual_raw_deflate(self):
        self.assertEqual(Bundle(fixture(b'abc' * 100, compressed=True)).content('Assets/test'), b'abc' * 100)

    def test_false_marker_in_payload(self):
        payload = bytes(8) + SIGNATURE
        self.assertEqual(Bundle(fixture(payload)).content('Assets/test'), payload)

    def test_two_valid_headers_are_ambiguous(self):
        first = fixture()
        with self.assertRaises(BundleError):
            Bundle(first + fixture(base=len(first)))

    def test_truncation(self):
        for length in (0, 8, 39, 42, len(fixture()) - 1):
            with self.subTest(length=length), self.assertRaises(BundleError):
                Bundle(fixture()[:length])

    def test_corrupt_pointer(self):
        for pointer in (-1, 0, 2**62):
            with self.subTest(pointer=pointer), self.assertRaises(BundleError):
                Bundle(struct.pack('<q', pointer) + fixture()[8:])

    def test_path_traversal_or_absolute(self):
        for name in ('../x', '/x', 'C:/x', 'Assets\\x', 'Assets//x', 'Assets/./x'):
            with self.subTest(name=name), self.assertRaises(BundleError):
                Bundle(fixture(names=(name,)))

    def test_case_insensitive_duplicates(self):
        with self.assertRaises(BundleError):
            Bundle(fixture(names=('Assets/test', 'assets/TEST')))

    def test_overlapping_content(self):
        with self.assertRaises(BundleError):
            Bundle(fixture(names=('Assets/a', 'Assets/b')))

    def test_range_and_version_count_limits(self):
        blob = fixture()
        header = 43
        entry = header + 12 + 13 + 40
        cases = [(header, '<I', 7), (header + 8, '<i', 4097), (entry, '<q', 2**62), (entry + 8, '<q', -1), (entry + 24, '<B', 255)]
        for offset, kind, value in cases:
            changed = bytearray(blob)
            struct.pack_into(kind, changed, offset, value)
            with self.subTest(offset=offset, value=value), self.assertRaises(BundleError):
                Bundle(bytes(changed))


if __name__ == '__main__':
    unittest.main()
