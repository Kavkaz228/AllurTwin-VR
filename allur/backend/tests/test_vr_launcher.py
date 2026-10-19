import json
import subprocess
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient as AnonymousClient
from sqlalchemy import select

from auth_client import TestClient
from app.database import User
from app.main import create_app
from app import vr_launcher as launcher


@pytest.fixture
def app(tmp_path, monkeypatch):
    monkeypatch.setenv('FUSION_ENABLED', 'false')
    return create_app('sqlite:///' + str(tmp_path / 'launch.db'), runner_enabled=False)


def test_launch_requires_login_csrf_and_operator_role(app, monkeypatch):
    monkeypatch.setattr(launcher.subprocess, 'run', lambda *a, **kw: pytest.fail('Must not launch'))
    with AnonymousClient(app) as client:
        assert client.get('/api/fusion/vr').status_code == 401
        assert client.post('/api/fusion/vr/launch', json={}).status_code == 401
    with TestClient(app) as client:
        assert client.post('/api/fusion/vr/launch', json={}, headers={'X-CSRF-Token': ''}).status_code == 403
        with app.state.service.sessions.begin() as db:
            db.scalar(select(User).where(User.username == 'admin')).role = 'viewer'
        app.state.security.invalidate()
        assert client.post('/api/fusion/vr/launch', json={}).status_code == 403


def test_launcher_uses_only_fixed_project_script_and_returns_existing_player(app, monkeypatch):
    calls = []
    monkeypatch.setattr(launcher, 'desktop_status', lambda: {'available': True, 'running': True, 'reason': ''})
    def run(argv, **options):
        calls.append((argv, options))
        return SimpleNamespace(returncode=0, stdout=b'{"alreadyRunning":true,"pid":123}')
    monkeypatch.setattr(launcher.subprocess, 'run', run)
    with TestClient(app) as client:
        assert client.get('/api/fusion/vr').json()['running'] is True
        assert not calls  # Read-only status never opens a program.
        result = client.post('/api/fusion/vr/launch', json={'path': 'untrusted.exe', 'args': 'not allowed'})
        assert result.status_code == 200 and result.json()['alreadyRunning'] is True
    argv, options = calls[0]
    assert argv[-1] == str(launcher.ROOT / 'Launch-VR.ps1')
    assert 'untrusted.exe' not in ' '.join(argv) and not options.get('shell')


def test_missing_build_and_busy_launch_do_not_start_process(app, monkeypatch):
    monkeypatch.setattr(launcher.subprocess, 'run', lambda *a, **kw: pytest.fail('Must not launch'))
    monkeypatch.setattr(launcher, 'desktop_status', lambda: {'available': False, 'running': False})
    with TestClient(app) as client:
        assert client.post('/api/fusion/vr/launch', json={}).status_code == 503
        monkeypatch.setattr(launcher, 'desktop_status', lambda: {'available': True, 'running': False})
        with launcher.LOCK:
            assert client.post('/api/fusion/vr/launch', json={}).status_code == 409


def test_timeout_releases_launch_lock(app, monkeypatch):
    monkeypatch.setattr(launcher, 'desktop_status', lambda: {'available': True, 'running': False})
    def timeout(*args, **kwargs):
        raise subprocess.TimeoutExpired('fixed launcher', 17)
    monkeypatch.setattr(launcher.subprocess, 'run', timeout)
    with TestClient(app) as client:
        assert client.post('/api/fusion/vr/launch', json={}).status_code == 504
    assert not launcher.LOCK.locked()


def test_status_validates_actual_process_path_not_registry_claims(tmp_path, monkeypatch):
    (tmp_path / 'runtime').mkdir()
    (tmp_path / 'runtime/processes.json').write_text(json.dumps({'vr': {'pid': 123, 'executable': 'untrusted.exe'}}))
    monkeypatch.setattr(launcher, 'ROOT', tmp_path)
    def matches(pid, expected):
        assert pid == 123 and expected == launcher.PLAYER
        return False
    monkeypatch.setattr(launcher, 'process_matches', matches)
    assert launcher.desktop_status()['running'] is False


def test_native_fatal_dialog_is_not_a_running_vr_scene(tmp_path, monkeypatch):
    monkeypatch.setenv('ALLUR_RUNTIME_DIR', str(tmp_path))
    (tmp_path / 'processes.json').write_text(json.dumps({'vr': {'pid': 123}}))
    monkeypatch.setattr(launcher, 'process_matches', lambda *args: True)
    log = tmp_path / 'vr-player.log'
    for text in ('Unable to load mono library', 'Initialize engine version: test', ''):
        log.write_text(text)
        assert launcher.desktop_status()['running'] is False
    log.write_text('XRGeneral Settings awakening...')
    state = launcher.desktop_status()
    assert state['starting'] and not state['running']
    log.write_text('[Workshop teleport] initialized')
    assert launcher.desktop_status()['running'] is True
    log.write_text('[Workshop teleport] initialized\nXR_ERROR_RUNTIME_FAILURE')
    state = launcher.desktop_status()
    assert state['running'] and state['headsetError']
    log.write_text('[Workshop teleport] initialized\nFailed to load mono')
    assert launcher.desktop_status()['running'] is False


def test_missing_mono_marks_build_unavailable(tmp_path, monkeypatch):
    player = tmp_path / 'AllurTwinVR.exe'
    player.write_bytes(b'exe')
    monkeypatch.setattr(launcher, 'PLAYER', player)
    for name in launcher.REQUIRED:
        file = tmp_path / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(b'component')
    assert launcher.desktop_status()['available'] is True
    (tmp_path / 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll').unlink()
    state = launcher.desktop_status()
    assert not state['available'] and 'Перезапустите' in state['reason']
