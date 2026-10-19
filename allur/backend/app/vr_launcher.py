"""Local desktop launch of the fixed, authenticated VR player; no motion commands."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import threading

from fastapi import APIRouter, HTTPException, Request

router = APIRouter(prefix='/api/fusion/vr', tags=['VR desktop launcher'])
ROOT = Path(__file__).resolve().parents[3]
PLAYER = Path(os.getenv('ALLUR_PLAYER_DIR', str(ROOT / 'workshop/unity/Builds/Windows'))) / 'AllurTwinVR.exe'
REQUIRED = ('UnityPlayer.dll', 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll',
            'AllurTwinVR_Data/Managed/mscorlib.dll', 'AllurTwinVR_Data/Managed/Assembly-CSharp.dll',
            'AllurTwinVR_Data/globalgamemanagers', 'AllurTwinVR_Data/Plugins/x86_64/UnityOpenXR.dll')
LOCK = threading.Lock()


def process_matches(pid, expected):
    """A stale/reused PID must never be reported as our VR application."""
    if os.name != 'nt' or not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
        return False
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return False
    try:
        length = wintypes.DWORD(32768)
        value = ctypes.create_unicode_buffer(length.value)
        return bool(kernel.QueryFullProcessImageNameW(handle, 0, value, ctypes.byref(length))) and os.path.normcase(value.value) == os.path.normcase(str(expected))
    finally:
        kernel.CloseHandle(handle)


def desktop_status():
    available = os.name == 'nt' and PLAYER.is_file() and all((PLAYER.parent / name).is_file() for name in REQUIRED)
    running = False
    starting = False
    headset_error = False
    try:
        registry = json.loads((Path(os.getenv('ALLUR_RUNTIME_DIR', str(ROOT / 'runtime'))) / 'processes.json').read_text(encoding='utf-8-sig'))
        running = process_matches(registry.get('vr', {}).get('pid'), PLAYER)
        if running:
            log = (Path(os.getenv('ALLUR_RUNTIME_DIR', str(ROOT / 'runtime'))) / 'vr-player.log').read_text(encoding='utf-8', errors='replace')
            running = '[Workshop teleport] initialized' in log and not any(marker in log for marker in ('Unable to load mono', 'Failed to load mono'))
            headset_error = any(marker in log for marker in ('XR_ERROR_RUNTIME_FAILURE', 'Unrecoverable runtime error'))
            starting = not running and 'XRGeneral Settings awakening' in log and not any(marker in log for marker in ('Unable to load mono', 'Failed to load mono'))
    except (OSError, ValueError, AttributeError):
        running = False
    message = ('Цех инициализируется. Если загрузка задерживается, проверьте подключение очков в VIVE Console и SteamVR.' if starting else
               'Цех работает на мониторе, но OpenXR не смог подключить очки. Подключите VIVE Console к SteamVR, проверьте готовность шлема и перезапустите цех.' if headset_error else
               'Цех загружен. Управление роботами доступно на VR-пульте.' if running else '')
    return {'available': available, 'running': running, 'starting': starting, 'headsetError': headset_error, 'message': message,
            'reason': '' if available else 'Компоненты VR отсутствуют. Перезапустите AllurTwin для восстановления; если ошибка повторяется, распакуйте ZIP целиком.'}


@router.get('')
def status():
    return desktop_status()


@router.post('/launch')
def launch(request: Request):
    if request.state.identity['role'] not in ('admin', 'operator'):
        raise HTTPException(403, 'Запуск VR доступен оператору и администратору.')
    state = desktop_status()
    if not state['available']:
        raise HTTPException(503, state.get('reason', 'Компоненты VR не найдены. Перезапустите AllurTwin.'))
    if not LOCK.acquire(blocking=False):
        raise HTTPException(409, 'VR уже запускается. Подождите несколько секунд.')
    try:
        # Paths and all arguments are fixed by the application, never by an HTTP payload.
        powershell = Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
        result = subprocess.run([str(powershell), '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(ROOT / 'Launch-VR.ps1')], cwd=ROOT,
                                capture_output=True, timeout=40, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise HTTPException(503, 'Загрузка VR не подтверждена. Перезапустите AllurTwin для проверки компонентов. Подробности: runtime/vr-player.log.')
        details = json.loads(result.stdout.decode('utf-8-sig'))
        state = desktop_status()
        if state.get('starting'):
            return {**state, 'alreadyRunning': bool(details.get('alreadyRunning'))}
        if not state['running']:
            raise HTTPException(503, 'VR-приложение завершилось при запуске. Проверьте журнал runtime/vr-player.log.')
        return {**state, 'alreadyRunning': bool(details.get('alreadyRunning')),
                'message': 'Цех работает на мониторе, но OpenXR не смог подключить очки. Подключите VIVE Console к SteamVR, проверьте готовность шлема и перезапустите цех.' if state.get('headsetError') else
                'VR уже работает. Перейдите в окно AllurTwinVR или наденьте очки.' if details.get('alreadyRunning')
                else 'VR запущен. Дождитесь загрузки цеха и наденьте очки. Управление роботами доступно на VR-пульте.'}
    except subprocess.TimeoutExpired:
        raise HTTPException(504, 'Запуск занимает больше времени. Проверьте окно AllurTwinVR и повторите проверку.')
    except (OSError, ValueError):
        raise HTTPException(503, 'Не удалось связаться с локальным запуском VR. Проверьте установку проекта.')
    finally:
        LOCK.release()
