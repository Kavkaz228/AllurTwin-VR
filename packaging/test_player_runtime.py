import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('player_runtime', Path(__file__).with_name('player_runtime.py'))
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class PlayerRuntimeTests(unittest.TestCase):
    def bundle(self, root, files):
        archive = root / 'resources/vr-player.zip'
        archive.parent.mkdir(exist_ok=True)
        manifest = {name: {'size':len(data), 'sha256':hashlib.sha256(data).hexdigest()} for name, data in files.items()}
        with zipfile.ZipFile(archive, 'w') as bundle:
            for name, data in files.items():
                bundle.writestr(name, data)
            bundle.writestr('player-files.json', json.dumps(manifest))
        archive.with_suffix('.sha256').write_text(runtime.digest(archive))
        return archive

    def test_repairs_missing_mono_and_same_size_corruption_without_rewriting_good_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            files = {'AllurTwinVR.exe':b'executable', 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll':b'mono',
                     'AllurTwinVR_Data/Managed/Assembly-CSharp.dll':b'code'}
            self.bundle(root, files)
            target = runtime.prepare_player(root, root/'data')
            stamp = (target/'AllurTwinVR.exe').stat().st_mtime_ns
            (target/'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll').unlink()
            (target/'AllurTwinVR_Data/Managed/Assembly-CSharp.dll').write_bytes(b'xxxx')
            self.assertEqual(runtime.prepare_player(root, root/'data'), target)
            self.assertEqual((target/'AllurTwinVR.exe').stat().st_mtime_ns, stamp)
            for name, data in files.items():
                self.assertEqual((target/name).read_bytes(), data)

    def test_corrupt_archive_is_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            archive = self.bundle(root, {'AllurTwinVR.exe':b'exe'})
            with archive.open('ab') as output:
                output.write(b'corruption')
            with self.assertRaisesRegex(RuntimeError, 'повреждён'):
                runtime.prepare_player(root, root/'data')
            self.assertFalse((root/'data').exists())

    def test_archive_cannot_escape_player_directory(self):
        for name in ('../outside.exe', '/outside.exe', 'C:/outside.exe', 'folder\\outside.exe'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                self.bundle(root, {name:b'exe'})
                with self.assertRaisesRegex(RuntimeError, 'Недопустимый путь'):
                    runtime.prepare_player(root, root/'data')
                self.assertFalse((root/'data').exists())


if __name__ == '__main__':
    unittest.main()
