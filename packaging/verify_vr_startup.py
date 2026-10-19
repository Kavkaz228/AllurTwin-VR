"""Check the actual desktop instance without exposing its local credentials."""
import http.cookiejar
import json
import os
from pathlib import Path
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
data = Path(os.environ['LOCALAPPDATA']) / 'AllurTwinVR'
credentials = json.loads((data/'access.json').read_text())
client = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
csrf = ''


def request(path, body=None, headers=None):
    req = urllib.request.Request('http://127.0.0.1:8091'+path, data=None if body is None else json.dumps(body).encode(),
                                 headers={'Content-Type':'application/json', 'X-CSRF-Token':csrf, **(headers or {})})
    with client.open(req, timeout=25) as response:
        return json.load(response)


ticket = request('/api/desktop/ticket', {}, {'X-Desktop-Secret':credentials['desktopSecret']})['ticket']
identity = request('/api/desktop/session', {'ticket':ticket})
csrf = identity['csrf']
assert identity['role'] == 'admin'
state = request('/api/fusion/state')
assert state['connected'] and len(state['workshop']['robots']) == 4
vr = request('/api/fusion/vr/launch', {})
assert vr['running'] and vr['alreadyRunning'], vr
first = json.loads((data/'runtime/processes.json').read_text(encoding='utf-8-sig'))['vr']
again = request('/api/fusion/vr/launch', {})
second = json.loads((data/'runtime/processes.json').read_text(encoding='utf-8-sig'))['vr']
assert again['alreadyRunning'] and first == second
assert Path(first['executable']).is_relative_to(data/'player')
for _ in range(15):
    log = (data/'runtime/vr-player.log').read_text(encoding='utf-8', errors='replace')
    if 'telemetry received for station 4' in log:
        break
    time.sleep(1)
assert '[Workshop teleport] initialized' in log
assert 'server authentication accepted' in log
assert 'telemetry received for station 4' in log
assert 'Unable to load mono' not in log and 'Failed to load mono' not in log
report = {'monoLoaded':True, 'sceneLoaded':True, 'authenticated':True, 'robotStations':4,
          'duplicateLaunchPrevented':True, 'player':first['executable'], 'pid':first['pid'],
          'headsetError':vr.get('headsetError'), 'statusMessage':vr['message']}
(ROOT/'release/verification-mono-fix.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
