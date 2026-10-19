"""Build a redistributable directory from an explicit allowlist; never copy local secrets."""
import argparse
import os
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = ROOT.parents[1]
DEST = ROOT/'release/v1.1/AllurTwin-VR'
PYTHON = Path(sys.base_prefix)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, target, dirs_exist_ok=True, ignore=shutil.ignore_patterns('__pycache__','*.pyc','*.pyo','.pytest_cache'))
    else:
        shutil.copy2(source, target)


def build():
    DEST.mkdir(parents=True, exist_ok=True)
    for name in ('allur/backend/app','allur/frontend/dist','allur/samples','workshop/server','workshop/public','workshop/node_modules/ws','workshop/package.json','workshop/package-lock.json','Launch-VR.ps1','SOURCE-PROVENANCE.json','requirements.local.lock'):
        print('Copy',name,flush=True); copy(ROOT/name,DEST/name)
    for name in ('supervisor.py','api.py','web.cjs','player_runtime.py'):
        copy(ROOT/'packaging'/name,DEST/'packaging'/name)
    build_player_bundle(DEST)
    copy(ROOT/'packaging/START-HERE.txt',DEST/'НАЧНИТЕ ЗДЕСЬ.txt')
    copy(Path(shutil.which('node')),DEST/'runtimes/node/node.exe')
    pyout=DEST/'runtimes/python'
    for file in PYTHON.iterdir():
        if file.is_file() and (file.suffix in ('.exe','.dll') or file.name=='LICENSE.txt'):
            copy(file,pyout/file.name)
    copy(PYTHON/'DLLs',pyout/'DLLs')
    for item in (PYTHON/'Lib').iterdir():
        if item.name not in ('site-packages','__pycache__','test','idlelib','tkinter','turtledemo','ensurepip'):
            copy(item,pyout/'Lib'/item.name)
    copy(ROOT/'.venv/Lib/site-packages',pyout/'Lib/site-packages')
    print('Copy Ollama and Qwen3 weights…',flush=True)
    runtime = Path(os.environ.get('ALLUR_OLLAMA_RUNTIME', str(WORKSPACE/'tools/ollama/runtime/v0.40.3')))
    copy(runtime,DEST/'runtimes/ollama')
    modelroot=Path(os.environ.get('ALLUR_OLLAMA_MODELS', str(WORKSPACE/'data/models')))
    manifest=Path('manifests/registry.ollama.ai/library/qwen3/4b')
    copy(modelroot/manifest,DEST/'models'/manifest)
    spec=json.loads((modelroot/manifest).read_text())
    for layer in [spec['config'],*spec['layers']]:
        name=layer['digest'].replace(':','-')
        copy(modelroot/'blobs'/name,DEST/'models/blobs'/name)
    copy(ROOT/'packaging/licenses',DEST/'licenses')
    for folder in (ROOT/'allur/frontend/node_modules').iterdir():
        candidates = list(folder.iterdir()) if folder.name.startswith('@') and folder.is_dir() else [folder]
        for package in candidates:
            if not package.is_dir(): continue
            for file in package.iterdir():
                if file.is_file() and file.name.lower().startswith(('license','notice','copying')):
                    copy(file,DEST/'licenses/frontend'/package.relative_to(ROOT/'allur/frontend/node_modules')/file.name)
    copy(ROOT/'workshop/unity/Assets/VRWorkshop/Textures/PolyHaven/concrete_floor_02/LICENSE.txt',DEST/'licenses/PolyHaven-concrete.txt')
    compiler=Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
    subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',str(ROOT/'packaging/icon.ps1'),'-OutputPath',str(ROOT/'packaging/AllurTwin.ico')],check=True)
    subprocess.run([str(compiler),'/nologo','/target:winexe','/platform:x64','/optimize+',
        '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll',
        '/win32icon:'+str(ROOT/'packaging/AllurTwin.ico'),'/win32manifest:'+str(ROOT/'packaging/launcher.manifest'),
        '/out:'+str(DEST/'AllurTwin.exe'),str(ROOT/'packaging/Launcher.cs')],check=True)
    copy(ROOT/'packaging/AllurTwin.ico',DEST/'AllurTwin.ico')
    write_inventory(DEST)
    print('Ready:',DEST,flush=True)


def write_inventory(destination):
    # Python dependencies retain their original dist-info license metadata.
    report=[]
    for file in sorted(destination.rglob('*')):
        if not file.is_file() or file.name=='FILES.sha256': continue
        with file.open('rb') as stream:
            digest=hashlib.file_digest(stream,'sha256').hexdigest()
        report.append(digest+'  '+file.relative_to(destination).as_posix())
    (destination/'FILES.sha256').write_text('\n'.join(report)+'\n',encoding='utf-8')


def build_player_bundle(destination):
    source = ROOT/'workshop/unity/Builds/Windows'
    target = destination/'resources/vr-player.zip'
    target.parent.mkdir(parents=True, exist_ok=True)
    manifest = {}
    with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=1) as bundle:
        for file in sorted(source.rglob('*')):
            if not file.is_file() or 'Diagnostics' in file.relative_to(source).parts:
                continue
            name = file.relative_to(source).as_posix()
            with file.open('rb') as stream:
                checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
            manifest[name] = {'size':file.stat().st_size, 'sha256':checksum}
            bundle.write(file, name)
        bundle.writestr('player-files.json', json.dumps(manifest, indent=2))
    with target.open('rb') as stream:
        checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
    target.with_suffix('.sha256').write_text(checksum+'\n', encoding='ascii')


def archive():
    target=ROOT/'release/AllurTwin-VR-Windows-x64-v1.1.zip'
    write_inventory(DEST)
    print('Creating ZIP64 archive…',flush=True)
    with zipfile.ZipFile(target,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=1,allowZip64=True) as output:
        for file in sorted(DEST.rglob('*')):
            if file.is_file(): output.write(file,Path('AllurTwin-VR')/file.relative_to(DEST))
    with target.open('rb') as file: digest=hashlib.file_digest(file,'sha256').hexdigest()
    target.with_suffix('.zip.sha256').write_text(digest+'  '+target.name+'\n',encoding='utf-8')
    print(json.dumps({'zip':str(target),'bytes':target.stat().st_size,'sha256':digest}),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('--zip-only',action='store_true');parser.add_argument('--no-zip',action='store_true');args=parser.parse_args()
    if not args.zip_only: build()
    if not args.no_zip: archive()
