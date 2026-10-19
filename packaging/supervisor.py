"""Owns only bundled child processes. Windows launcher places us in a kill-on-close job."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time
import traceback
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'packaging'))
from player_runtime import prepare_player
DATA = Path(os.environ['ALLUR_PORTABLE_DATA'])
RUNTIME = DATA / 'runtime'
RUNTIME.mkdir(parents=True, exist_ok=True)
children = {}
logs = []
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def status(message, state='starting'):
    temp = RUNTIME / 'launcher.tmp'
    temp.write_text(json.dumps({'state': state, 'message': message}, ensure_ascii=False), encoding='utf-8')
    os.replace(temp, RUNTIME / 'launcher.json')


def request(url, body=None, headers=None, timeout=2):
    req = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(), headers=headers or {})
    with opener.open(req, timeout=timeout) as response:
        return json.load(response)


def start(name, command, env, health):
    stream = open(RUNTIME / (name + '.log'), 'wb'); logs.append(stream)
    proc = subprocess.Popen([str(x) for x in command], cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                            stdout=stream, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    children[name] = proc
    for _ in range(180):
        if proc.poll() is not None:
            raise RuntimeError(name + ': запуск завершился с ошибкой. Откройте журнал ' + name + '.log')
        try:
            request(health)
            return
        except Exception:
            time.sleep(.25)
    raise RuntimeError(name + ': превышено время запуска. Откройте журналы.')


def launch_vr(env):
    powershell = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    result = subprocess.run([str(powershell), '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(ROOT/'Launch-VR.ps1')],
                            env=env, cwd=ROOT, capture_output=True, timeout=40, creationflags=subprocess.CREATE_NO_WINDOW)
    (RUNTIME / 'vr-launch.log').write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError('VR не открылся. См. vr-launch.log; панель и серверы продолжают работать.')
    details = json.loads(result.stdout.decode('utf-8-sig'))
    if details.get('starting'):
        return 'Цех инициализируется. Проверьте готовность очков в VIVE Console и SteamVR.'
    if details.get('headsetError'):
        return 'Цех открыт на мониторе. Подключите очки в VIVE Console и SteamVR, затем перезапустите цех.'
    return 'VR-цех открыт. Наденьте очки.'


def run():
    # The launcher releases this gate only after AssignProcessToJobObject succeeds.
    gate = Path(sys.argv[1])
    for _ in range(200):
        if gate.exists():
            gate.unlink()
            break
        time.sleep(.05)
    else:
        raise RuntimeError('Не удалось связать процессы с приложением запуска.')
    for port in (8091, 8184, 4184, 11436):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            try:
                probe.bind(('127.0.0.1', port))
            except OSError:
                raise RuntimeError('Порт %s занят. Закройте другой экземпляр AllurTwin или программу на этом порту.' % port)
    access = DATA / 'access.json'
    if not access.exists():
        with access.open('x', encoding='utf-8') as file:
            json.dump({'username':'admin','password':secrets.token_urlsafe(40),'vrToken':secrets.token_urlsafe(40),'desktopSecret':secrets.token_urlsafe(40)}, file)
    credentials = json.loads(access.read_text(encoding='utf-8-sig'))
    if any(len(credentials.get(k, '')) < 40 for k in ('password', 'vrToken', 'desktopSecret')):
        raise RuntimeError('Повреждён локальный файл доступа: ' + str(access))
    status('Проверяем и подготавливаем компоненты VR…')
    player_dir = prepare_player(ROOT, DATA)
    env = os.environ.copy()
    # Do not inherit development settings, Python paths, proxy settings or cloud credentials.
    for key in list(env):
        if key.startswith(('PYTHON','OLLAMA','VR_','DATABASE_','TLS_')) or key in ('CONFIG_FILE','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY'):
            env.pop(key, None)
    env.update({
        'HOST':'127.0.0.1','PORT':'4184','DATA_DIR':str(DATA/'workshop'),
        'VR_ACCESS_TOKEN':credentials['vrToken'],'VR_WORKSHOP_ACCESS_TOKEN':credentials['vrToken'],
        'VR_WORKSHOP_CONNECT_URL':'ws://127.0.0.1:4184/ws','VR_WORKSHOP_URL':'http://127.0.0.1:4184',
        'OLLAMA_URL':'http://127.0.0.1:11436','OLLAMA_BASE_URL':'http://127.0.0.1:11436',
        'OLLAMA_MODEL':'qwen3:4b','AI_MODEL':'qwen3:4b','OLLAMA_TIMEOUT_MS':'120000',
        'ALLUR_RUNTIME_DIR':str(RUNTIME),'ALLUR_ACCESS_FILE':str(access),
        'ALLUR_PLAYER_DIR':str(player_dir),
        'ALLUR_BRIEF_FILE':str(RUNTIME/'allur-brief.json'),
        'DATABASE_URL':'sqlite:///' + (DATA/'allur.db').as_posix(),
        'BOOTSTRAP_PASSWORD':credentials['password'],'BOOTSTRAP_PASSWORD_FILE':'',
        'COOKIE_SECURE':'false','SESSION_COOKIE_NAME':'allur_portable_session',
        'PUBLIC_ORIGIN':'http://127.0.0.1:8091','RUNNER_ENABLED':'true',
        'MONITORING_AI_ENABLED':'false','PHYSICAL_CONTROL_ENABLED':'false','FUSION_ENABLED':'true',
        'ALLUR_DESKTOP':'1','ALLUR_DESKTOP_SECRET':credentials['desktopSecret'],
    })
    status('Запускаем локальный ИИ…')
    profile = DATA/'ollama-profile'; profile.mkdir(exist_ok=True)
    ai_env = {**env, 'USERPROFILE':str(profile),'OLLAMA_HOST':'127.0.0.1:11436',
              'OLLAMA_MODELS':str(ROOT/'models'),'OLLAMA_NO_CLOUD':'true',
              'OLLAMA_CONTEXT_LENGTH':'4096','OLLAMA_NUM_PARALLEL':'1','OLLAMA_MAX_LOADED_MODELS':'1','OLLAMA_KEEP_ALIVE':'5m'}
    start('ollama', [ROOT/'runtimes/ollama/ollama.exe','serve'], ai_env, 'http://127.0.0.1:11436/api/tags')
    tags = request('http://127.0.0.1:11436/api/tags')
    if not any(m['name']=='qwen3:4b' for m in tags.get('models', [])):
        raise RuntimeError('В архиве не найдена модель qwen3:4b. Распакуйте ZIP целиком.')
    status('Запускаем симуляцию роботов…')
    node = ROOT/'runtimes/node/node.exe'
    start('workshop', [node, ROOT/'workshop/server/index.js'], env, 'http://127.0.0.1:4184/health')
    status('Подключаем аналитику и сохранения…')
    start('api', [sys.executable, '-I', '-B', ROOT/'packaging/api.py'], env, 'http://127.0.0.1:8184/api/health')
    start('web', [node, ROOT/'packaging/web.cjs'], env, 'http://127.0.0.1:8091/desktop-health')
    # VR writes its own registry independently; service ownership is in a separate file.
    (RUNTIME/'services.json').write_text(json.dumps({name:proc.pid for name,proc in children.items()}), encoding='utf-8')
    status('Открываем VR-цех…')
    try:
        message = 'Готово. Панель, роботы и локальный ИИ работают.'
        if os.getenv('ALLUR_SKIP_AUTO_VR') != '1':
            message = launch_vr(env)
    except Exception as exc:
        message = str(exc)
    status(message, 'ready')
    last_vr_message = message
    while True:
        for name, proc in children.items():
            if proc.poll() is not None:
                raise RuntimeError(name + ' остановился. Перезапустите приложение; сохранения останутся на месте.')
        signal = RUNTIME/'open-vr.request'
        if signal.exists():
            signal.unlink()
            try:
                last_vr_message = launch_vr(env)
                status(last_vr_message, 'ready')
            except Exception as exc:
                status(str(exc), 'ready')
        if (RUNTIME/'stop.request').exists():
            break
        # Refresh a slow startup result after OpenXR finishes initializing.
        try:
            vr_log = ((RUNTIME/'vr-player.log').read_text(encoding='utf-8', errors='replace')
                      if 'инициализируется' in last_vr_message else '')
            if '[Workshop teleport] initialized' in vr_log:
                vr_message = ('Цех открыт на мониторе. Подключите очки в VIVE Console и SteamVR, затем перезапустите цех.'
                              if 'XR_ERROR_RUNTIME_FAILURE' in vr_log or 'Unrecoverable runtime error' in vr_log else 'VR-цех открыт. Наденьте очки.')
                if vr_message != last_vr_message:
                    status(vr_message, 'ready')
                    last_vr_message = vr_message
        except OSError:
            pass
        time.sleep(.5)


try:
    run()
except Exception as exc:
    (RUNTIME/'supervisor-error.log').write_text(traceback.format_exc(), encoding='utf-8')
    status(str(exc), 'error')
    sys.exit(1)
finally:
    for proc in reversed(list(children.values())):
        if proc.poll() is None:
            proc.terminate()
    for stream in logs:
        stream.close()
