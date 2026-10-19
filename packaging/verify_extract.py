"""Validate distribution inventory and extract the real ZIP into a fresh relocated folder."""
import hashlib
import argparse
import json
from pathlib import Path
import zipfile

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--version', default='')
args=parser.parse_args()
suffix='-'+args.version if args.version else ''
target=ROOT/('.test-temp/Распакованный архив'+suffix)/'AllurTwin-VR'
if target.exists():
    raise RuntimeError('Use a fresh extraction directory; previous test files are preserved.')
archive=ROOT/('release/AllurTwin-VR-Windows-x64'+suffix+'.zip')
with zipfile.ZipFile(archive) as zip:
    names=zip.namelist()
    assert 'AllurTwin-VR/AllurTwin.exe' in names
    if args.version:
        assert 'AllurTwin-VR/resources/vr-player.zip' in names
        assert not any('/workshop/unity/' in name for name in names)
    forbidden={'access.json','allur.db','allur-vr.db','bootstrap-password','processes.json','services.json'}
    for name in names:
        parts=Path(name).parts
        assert '..' not in parts and parts[0]=='AllurTwin-VR'
        assert not (set(parts)&{'.local','.venv','runtime','__pycache__'}),name
        assert Path(name).name not in forbidden,name
        assert not name.endswith(('.db','.db-wal','.db-shm','.log','.pyc')),name
    zip.extractall(target.parent)
print('PASS: ZIP extraction and no private runtime data',flush=True)
count=0
for line in (target/'FILES.sha256').read_text(encoding='utf-8').splitlines():
    expected,name=line.split('  ',1)
    with (target/name).open('rb') as file:
        actual=hashlib.file_digest(file,'sha256').hexdigest()
    assert actual==expected,name
    count+=1
result={'verifiedFiles':count,'zipBytes':archive.stat().st_size,'extractedPath':str(target),'privateRuntimeData':False}
(ROOT/('release/verification-files'+suffix+'.json')).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS: SHA256 for %s extracted files' % count,flush=True)
