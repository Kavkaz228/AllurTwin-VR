import copy
import json
import math
import time
from urllib.error import URLError, HTTPError

import pytest
from fastapi.testclient import TestClient as AnonymousClient
from sqlalchemy import select, func

from auth_client import TestClient
from app.database import Observation, QualityRecord, ScadaRecord
from app.fusion import Fusion, PASSPORT_KIND, observation, ai_timeout_seconds, ai_failure_notice, inference_context
from app.main import create_app
from app.service import Service


@pytest.fixture
def service(tmp_path):
    value = Service('sqlite:///' + str(tmp_path / 'fusion.db'))
    yield value
    value.close()


def snapshot(now, epoch='epoch-one', body='panel-00001'):
    return {'serverSessionId': epoch, 'robots': [{
        'robotId': 'training-arm', 'serverTime': now * 1000, 'name': 'Сварка 1',
        'connected': True, 'joints': [0, math.pi / 2, -math.pi / 2, .2, .3, .4],
        'moving': True, 'fault': None, 'process': 'welding', 'lineId': 'welding',
        'production': {'running': True, 'stations': [{'robotId': 'training-arm', 'cycles': 2, 'stepProgress': .25}]}}],
        'productionLines': [{'process': 'welding', 'parts': [{'id': 'panel-00002', 'processProgress': .25}]}], 'transfer': {'parts': []},
        'finishing': {'parts': [{'id': body, 'status': 'parked', 'quality': 'passed', 'ready': True,
            'slotIndex': 0, 'weldProgress': 1, 'coating': 1, 'assemblyProgress': 1,
            'qualityChecks': [{'name': 'Сварка', 'measured': 1, 'required': 1, 'passed': True}]}]}}


def test_joint_mapping_does_not_fabricate_physical_sensors():
    robot = snapshot(100)['robots'][0]
    row = observation(robot)
    assert row['joint_2_deg'] == 90
    assert row['joint_3_deg'] == -90
    assert row['cycle_progress_pct'] == 25
    assert row['operation'] == 'welding'
    assert row['controller_mode'] == 'automatic'
    assert not {'joint_temperature_c', 'vibration_mm_s', 'hydraulic_pressure_bar', 'speed_percent'} & row.keys()
    assert observation({**robot, 'joints': []}) is None
    assert observation({**robot, 'joints': [float('nan')]}) is None


def test_local_model_timeout_has_cold_start_budget_and_upper_bound(monkeypatch):
    monkeypatch.delenv('FUSION_AI_TIMEOUT_SECONDS', raising=False)
    assert ai_timeout_seconds() == 90
    for configured, expected in [('120', 120), ('300', 120), ('45', 45), ('0', 1), ('invalid', 90)]:
        monkeypatch.setenv('FUSION_AI_TIMEOUT_SECONDS', configured)
        assert ai_timeout_seconds() == expected


def test_model_errors_distinguish_timeout_connection_and_missing_model():
    assert 'отведённое время' in ai_failure_notice(TimeoutError())
    assert 'Нет соединения' in ai_failure_notice(URLError(ConnectionRefusedError()))
    assert 'не найдена' in ai_failure_notice(HTTPError('http://127.0.0.1', 404, 'missing', {}, None))


def test_ai_context_carries_real_car_stages_not_waiting_reason(service):
    now = 1792400000.0
    data = snapshot(now)
    data['finishing']['waitingReason'] = 'Ожидание автомобиля после окраски'
    data['productionLines'].append({'process': 'painting', 'parts': []})
    bridge = Fusion(service, clock=lambda: now, fetcher=lambda: data)
    bridge.poll_once()
    context = inference_context(bridge.state(), bridge.brief())
    assert context['currentStageCounts']['сварочная линия'] == 1
    assert context['currentStageCounts']['покрасочная линия'] == 0
    assert context['currentStageCounts']['стоянка'] == 1
    car = next(c for c in context['currentCars'] if c['id'] == 'panel-00002')
    assert car['stage'] == 'сварочная линия' and car['progress'] == .25
    data['transfer'] = {'length': 8, 'parts': [{'id': 'panel-00003', 'position': 4}]}
    bridge.poll_once()
    context = inference_context(bridge.state(), bridge.brief())
    assert context['currentStageCounts']['грузовой конвейер между сваркой и окраской'] == 1 and context['currentVehicleCount'] == 3
    assert next(c for c in context['currentCars'] if c['id'] == 'panel-00003')['progress'] == .5


def test_observation_and_quality_dedup_survive_bridge_restart_and_node_epoch(service, tmp_path):
    now = [1792400000.0]
    data = [snapshot(now[0])]
    bridge = Fusion(service, clock=lambda: now[0], fetcher=lambda: data[0], brief_file=tmp_path / 'brief.json')
    bridge.poll_once()
    first_id = bridge.state()['passports'][0]['id']
    assert bridge.state()['import'] == {'observations': 1, 'qualityRecords': 1, 'lastError': None}
    bridge.poll_once()
    assert bridge.state()['import']['observations'] == 1
    # Same saved snapshot after restarting bridge must not insert duplicate keys.
    again = Fusion(service, clock=lambda: now[0], fetcher=lambda: data[0])
    again.poll_once()
    assert again.state()['import']['observations'] == 1
    assert len(again.state()['passports']) == 1
    # A restarted Node simulator may reuse panel-00001; its epoch makes it a new car.
    now[0] += 6
    data[0] = snapshot(now[0], epoch='epoch-two')
    again.poll_once()
    assert again.state()['import']['qualityRecords'] == 2
    assert len({p['id'] for p in again.state()['passports']}) == 2
    assert first_id in {p['id'] for p in again.state()['passports']}
    with service.sessions() as db:
        assert db.scalar(select(func.count()).select_from(Observation)) == 2
        assert db.scalar(select(func.count()).select_from(QualityRecord)) == 2
    brief = json.loads((tmp_path / 'brief.json').read_text(encoding='utf-8'))
    assert brief['available'] is True
    assert brief['activeVehicles'] == 1 and brief['readyVehicles'] == 1
    assert brief['robotInsights'][0]['cycles'] == 2


def test_old_timestamp_or_network_loss_never_claims_live(service, tmp_path):
    now = [1792400000.0]
    data = snapshot(now[0])
    file = tmp_path / 'brief.json'
    bridge = Fusion(service, clock=lambda: now[0], fetcher=lambda: data, brief_file=file)
    bridge.poll_once()
    now[0] += 8
    bridge.poll_once()  # HTTP can succeed while robot telemetry itself is stale.
    assert bridge.state()['connected'] is True
    assert bridge.state()['stale'] is True
    assert bridge.state()['import']['observations'] == 1
    assert json.loads(file.read_text(encoding='utf-8'))['available'] is False
    bridge.fetcher = lambda: (_ for _ in ()).throw(ConnectionError('offline'))
    bridge.poll_once()
    assert bridge.state()['connected'] is False
    assert bridge.state()['stale'] is True
    assert bridge.state()['workshop']['finishing']['parts'][0]['id'] == 'panel-00001'
    assert len(bridge.state()['passports']) == 1


def test_one_stale_robot_and_future_clock_are_not_hidden_by_fresh_peers(service):
    now = 1792400000.0
    data = snapshot(now)
    other = copy.deepcopy(data['robots'][0])
    other.update(robotId='paint-arm', serverTime=(now - 10) * 1000)
    data['robots'].append(other)
    bridge = Fusion(service, clock=lambda: now, fetcher=lambda: data)
    bridge.poll_once()
    assert bridge.state()['stale'] is True
    assert bridge.state()['import']['observations'] == 0
    other['serverTime'] = (now + 10) * 1000
    bridge.poll_once()
    assert bridge.state()['stale'] is True


def test_only_final_quality_is_registered_and_rejected_car_stays_rejected(service):
    now = [1792400000.0]
    data = snapshot(now[0])
    part = data['finishing']['parts'][0]
    part.update(status='to_release', ready=False)
    bridge = Fusion(service, clock=lambda: now[0], fetcher=lambda: data)
    bridge.poll_once()
    assert bridge.state()['passports'] == []
    now[0] += 6
    data['robots'][0]['serverTime'] = now[0] * 1000
    part.update(status='rejected', quality='rejected', ready=False)
    bridge.poll_once()
    assert bridge.state()['passports'][0]['quality'] == 'rejected'
    assert bridge.brief()['qualityPassed'] == 0
    assert bridge.brief()['qualityRejected'] == 1


def test_api_keeps_session_auth_and_ai_fallback_is_explicit(tmp_path, monkeypatch):
    monkeypatch.setenv('FUSION_ENABLED', 'false')
    app = create_app('sqlite:///' + str(tmp_path / 'api.db'), runner_enabled=False)
    with AnonymousClient(app) as client:
        assert client.get('/api/fusion/state').status_code == 401
        assert client.post('/api/fusion/analyze', json={'question': 'Что происходит?'}).status_code == 401
    with TestClient(app) as client:
        response = client.get('/api/fusion/state')
        assert response.status_code == 200 and response.json()['stale'] is True
        answer = client.post('/api/fusion/analyze', json={'question': 'Что происходит?'}).json()
        assert answer['source'] == 'rules' and answer['fallback'] is True
        assert answer['commands_allowed'] is False and answer['model'] is None
        bridge = app.state.fusion
        bridge.fetcher = lambda: snapshot(time.time())
        bridge.poll_once()
        monkeypatch.setattr('app.fusion.local_ai.ollama', lambda *a, **kw: (_ for _ in ()).throw(TimeoutError()))
        answer = client.post('/api/fusion/analyze', json={'question': 'Что происходит?'}).json()
        assert answer['stale'] is False and answer['fallback'] is True and answer['source'] == 'rules'
        captured = {}
        def successful_model(*args, **kwargs):
            captured.update(kwargs)
            return {'message': {'content': json.dumps({'answer': 'Учебный автомобиль на стоянке.'})}}
        monkeypatch.setattr('app.fusion.local_ai.ollama', successful_model)
        monkeypatch.setenv('FUSION_AI_TIMEOUT_SECONDS', '90')
        answer = client.post('/api/fusion/analyze', json={'question': 'Где автомобиль?'}).json()
        assert answer['source'] == 'local-model' and answer['fallback'] is False and answer['commands_allowed'] is False
        assert captured['timeout'] == 90
        monkeypatch.setattr('app.fusion.local_ai.ollama', lambda *a, **kw: {'message': {'content': 'invalid JSON'}})
        answer = client.post('/api/fusion/analyze', json={'question': 'Где автомобиль?'}).json()
        assert answer['source'] == 'rules' and answer['fallback'] is True
        # GET never writes or starts movement; no control route exists.
        assert client.post('/api/fusion/control', json={}).status_code == 404
