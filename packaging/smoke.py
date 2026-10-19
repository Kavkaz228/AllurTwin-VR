"""Exercise the running packaged application, including auth, bridge, AI and VR."""
import http.cookiejar
import json
import os
from pathlib import Path
import sys
import time
import urllib.request

data=Path(sys.argv[1]); credentials=json.loads((data/'access.json').read_text())
jar=http.cookiejar.CookieJar()
client=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(jar))
csrf=''

def request(path, body=None, headers=None, timeout=180):
    opts={'Content-Type':'application/json',**({'X-CSRF-Token':csrf} if csrf else {}),**(headers or {})}
    req=urllib.request.Request('http://127.0.0.1:8091'+path,data=None if body is None else json.dumps(body).encode(),headers=opts)
    with client.open(req,timeout=timeout) as response: return json.load(response)

assert request('/desktop-health')['application']=='allur-desktop'
ticket=request('/api/desktop/ticket',{}, {'X-Desktop-Secret':credentials['desktopSecret']})['ticket']
identity=request('/api/desktop/session',{'ticket':ticket}); csrf=identity['csrf']
assert identity['role']=='admin' and not identity['must_change']
print('PASS: first-run automatic session and static proxy',flush=True)
for _ in range(20):
    fusion=request('/api/fusion/state')
    if fusion['connected']:break
    time.sleep(1)
assert fusion['connected'],fusion.get('error')
assert len(fusion['workshop']['robots'])==4
print('PASS: four robot telemetry / isolated server port',flush=True)
ai=request('/api/fusion/analyze',{'question':'Кратко: какие этапы проходит автомобиль в этом учебном цехе?'})
assert ai['source']=='local-model' and not ai['fallback'],ai
print('PASS: bundled Qwen model real answer; '+ai['answer'][:160],flush=True)
vr=request('/api/fusion/vr/launch',{})
assert vr['running'],vr
first=json.loads((data/'runtime/processes.json').read_text(encoding='utf-8-sig'))['vr']['pid']
again=request('/api/fusion/vr/launch',{})
second=json.loads((data/'runtime/processes.json').read_text(encoding='utf-8-sig'))['vr']['pid']
assert again['alreadyRunning'] and first==second
print('PASS: VR launch and duplicate launch keeps same PID '+str(first),flush=True)
report={'auth':True,'robots':4,'fusion':True,'aiSource':ai['source'],'model':ai['model'],'vrPid':first,'vrDuplicatePrevented':True}
(data/'runtime/smoke-result.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
