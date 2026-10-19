"""Verify and unpack the bundled Unity runtime outside deeply nested ZIP paths."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import zipfile


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def prepare_player(root, data):
    archive = root / 'resources/vr-player.zip'
    checksum = root / 'resources/vr-player.sha256'
    if not archive.is_file() or not checksum.is_file():
        raise RuntimeError('Не найдены компоненты VR. Распакуйте весь архив приложения заново.')
    expected = checksum.read_text(encoding='ascii').split()[0]
    if len(expected) != 64 or digest(archive) != expected:
        raise RuntimeError('Архив компонентов VR повреждён. Распакуйте приложение из исходного ZIP заново.')
    target = data / 'player' / expected[:16]
    with zipfile.ZipFile(archive) as bundle:
        manifest_bytes = bundle.read('player-files.json')
        manifest = json.loads(manifest_bytes)
        # Validate every path before writing any files.
        for name, spec in manifest.items():
            path = PurePosixPath(name)
            if (not name or path.is_absolute() or '..' in path.parts or '\\' in name
                    or ':' in name or path.as_posix() != name):
                raise RuntimeError('Недопустимый путь в архиве VR.')
            info = bundle.getinfo(name)
            if info.is_dir() or info.file_size != spec['size'] or (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise RuntimeError('Повреждён список компонентов VR.')
        for name, spec in manifest.items():
            file = target / name
            if file.is_file() and file.stat().st_size == spec['size'] and digest(file) == spec['sha256']:
                continue
            file.parent.mkdir(parents=True, exist_ok=True)
            temp = file.with_name(file.name + '.repairing')
            with bundle.open(name) as source, temp.open('wb') as output:
                while chunk := source.read(1024 * 1024):
                    output.write(chunk)
            if digest(temp) != spec['sha256']:
                temp.unlink()
                raise RuntimeError('Не удалось проверить компонент VR: ' + name)
            os.replace(temp, file)
        (target / 'player-files.json').write_bytes(manifest_bytes)
    return target
