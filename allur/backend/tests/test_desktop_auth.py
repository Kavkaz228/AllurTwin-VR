import hashlib
import time
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from app.main import create_app
from app.database import User

SECRET = 'desktop-test-secret-' * 4


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setenv('BOOTSTRAP_PASSWORD', 'Unique bootstrap test phrase 2026!')
    monkeypatch.setenv('ALLUR_DESKTOP', '1')
    monkeypatch.setenv('ALLUR_DESKTOP_SECRET', SECRET)
    monkeypatch.setenv('PUBLIC_ORIGIN', 'http://127.0.0.1:8091')
    monkeypatch.setenv('FUSION_ENABLED', 'false')
    app = create_app('sqlite:///' + str(tmp_path/'desktop.db'), runner_enabled=False)
    with TestClient(app, client=('127.0.0.1', 12345)) as current:
        yield current


def get_ticket(client):
    response = client.post('/api/desktop/ticket', headers={'X-Desktop-Secret':SECRET})
    assert response.status_code == 200
    return response.json()['ticket']


def test_handoff_is_one_time_and_preserves_csrf(client):
    token = get_ticket(client)
    response = client.post('/api/desktop/session', json={'ticket': token})
    assert response.status_code == 200
    assert response.json()['role'] == 'admin'
    assert response.json()['must_change'] is False
    assert 'HttpOnly' in response.headers['set-cookie']
    assert client.get('/api/fusion/vr').status_code == 200
    assert client.post('/api/fusion/vr/launch', json={}).status_code == 403
    assert client.post('/api/desktop/session', json={'ticket':token}).status_code == 401


def test_wrong_secret_origin_and_expired_ticket(client):
    assert client.post('/api/desktop/ticket').status_code == 403
    assert client.post('/api/desktop/ticket', headers={'X-Desktop-Secret':SECRET,'Origin':'https://evil.example'}).status_code == 403
    token = get_ticket(client)
    client.app.state.security.desktop_tickets[hashlib.sha256(token.encode()).hexdigest()] = time.monotonic()-1
    assert client.post('/api/desktop/session', json={'ticket':token}).status_code == 401
    assert client.get('/api/fusion/vr').status_code == 401


def test_desktop_disabled_by_default(client, monkeypatch):
    monkeypatch.delenv('ALLUR_DESKTOP')
    assert client.post('/api/desktop/ticket', headers={'X-Desktop-Secret':SECRET}).status_code == 401


def test_handoff_respects_disabled_account_and_role(client):
    with client.app.state.service.sessions.begin() as db:
        db.scalar(select(User).where(User.username=='admin')).active = False
    assert client.post('/api/desktop/session', json={'ticket':get_ticket(client)}).status_code == 403
    with client.app.state.service.sessions.begin() as db:
        user=db.scalar(select(User).where(User.username=='admin')); user.active=True; user.role='viewer'
    response=client.post('/api/desktop/session', json={'ticket':get_ticket(client)})
    assert response.status_code==200 and response.json()['role']=='viewer'
    assert client.post('/api/fusion/vr/launch',json={},headers={'X-CSRF-Token':response.json()['csrf']}).status_code==403
