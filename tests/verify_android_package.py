"""Read-only checks for the APK archive and its current frontend assets."""
from pathlib import Path
import struct
import zipfile

root = Path(__file__).resolve().parents[1]
apk = root / 'android-dist/ERA-AI-Loader.apk'
with zipfile.ZipFile(apk) as archive:
    assert archive.testzip() is None, 'APK ZIP CRC failure'
    count = 0
    for source in (root / 'portable/era-core/EraCore.Web/dist-maui').rglob('*'):
        if not source.is_file():
            continue
        name = 'assets/wwwroot/' + source.relative_to(root / 'portable/era-core/EraCore.Web/dist-maui').as_posix()
        assert archive.read(name) == source.read_bytes(), f'Stale packaged asset: {name}'
        count += 1
    print(f'PASS: all {count} built frontend files exactly match APK assets')
    libraries = [name for name in archive.namelist() if name.startswith('lib/arm64-v8a/') and name.endswith('.so')]
    assert libraries, 'No ARM64 libraries'
    for name in libraries:
        data = archive.read(name)
        assert data[:6] == b'\x7fELF\x02\x01', f'Not little-endian ELF64: {name}'
        offset = struct.unpack_from('<Q', data, 32)[0]
        size, entries = struct.unpack_from('<HH', data, 54)
        for i in range(entries):
            position = offset + i * size
            if struct.unpack_from('<I', data, position)[0] == 1:
                assert struct.unpack_from('<Q', data, position + 48)[0] >= 16384, f'Unaligned ELF: {name}'
    print(f'PASS: ZIP integrity and 16 KB ELF alignment for {len(libraries)} ARM64 libraries')
