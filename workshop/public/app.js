const $ = id => document.getElementById(id);
let ws, id = 0, sequence = 0, robotId, leaseId = '', telemetry, arrived = 0, jog = null, yaw = .6;
const pending = new Map();
const log = text => { const e = $('events'); if(e.textContent === 'Событий пока нет.') e.textContent=''; e.textContent = `${new Date().toLocaleTimeString()}  ${text}\n` + e.textContent.slice(0,5000); };
function send(type, fields = {}, callback) {
  if(ws?.readyState !== WebSocket.OPEN) return;
  const requestId = String(++id);
  if(callback) pending.set(requestId, { callback, expires: performance.now()+25000 });
  ws.send(JSON.stringify({ type, id:requestId, robotId, leaseId, ...fields }));
}
function forget() { jog = null; leaseId=''; telemetry=null; pending.clear(); $('connection').textContent='ОТКЛЮЧЕНО'; }
function disconnect() { if(leaseId) send('release'); ws?.close(); ws=null; forget(); }
$('connect').onclick = () => {
  disconnect();
  const socket = new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}/ws`); ws=socket;
  socket.onopen = () => send('auth',{protocolVersion:1,accessToken:$('token').value,clientName:$('name').value});
  socket.onmessage = ({data}) => {
    if(ws!==socket) return;
    const m=JSON.parse(data);
    if(m.type==='welcome') {
      $('connection').textContent='ПОДКЛЮЧЕНО';
      $('robot').replaceChildren(...m.robots.map(r=>new Option(r.name,r.id)));
      robotId=$('robot').value; send('subscribe'); refresh(); log('Соединение с сервером установлено');
    } else if(m.type==='telemetry' && m.robotId===robotId) {
      telemetry=m; arrived=performance.now(); show(m);
      if(m.fault || !m.connected) jog=null;
    } else if(m.type==='ack') {
      const request=pending.get(m.requestId); pending.delete(m.requestId); request?.callback(m);
      if(!m.ok) { log(m.error); $('state').textContent=m.error; jog=null; }
    } else if(m.type==='ai.result') $('advice').textContent=m.ok ? `${m.summary}\n${(m.notes||[]).join('\n')}` : `ИИ недоступен: ${m.error}`;
    else if(m.type==='validation.result') $('validation').textContent=m.ok ? (m.allowed?'Проверка пройдена для указанной модели и записи.':`Отклонено: ${m.issues.join('; ')}`) : `Проверка недоступна: ${m.error}`;
  };
  socket.onclose=()=>{if(ws===socket) {forget(); log('Связь потеряна. Повторное подключение — вручную.');}};
  socket.onerror=()=>log('Ошибка подключения. Проверьте адрес, TLS и доступность сервера.');
};
$('disconnect').onclick=disconnect;
$('robot').onchange=()=>{if(leaseId) send('release'); leaseId=''; telemetry=null; jog=null; robotId=$('robot').value; send('subscribe');};
$('acquire').onclick=()=>send('acquire',{},m=>{if(m.ok){leaseId=m.leaseId;sequence=0;log('Право управления получено');}});
$('release').onclick=()=>{send('release');leaseId='';jog=null;};
$('stop').onclick=()=>{jog=null;send('stop');};
$('reset').onclick=()=>{jog=null;deadmanOff();send('reset');};
function deadmanOff(){ if(leaseId) send('command',{sequence:++sequence,deadman:false}); }
for(const button of document.querySelectorAll('[data-axis]')) {
  button.onpointerdown=e=>{
    if(!leaseId || !telemetry?.connected || telemetry.fault || performance.now()-arrived>200) return;
    button.setPointerCapture(e.pointerId); jog={axis:button.dataset.axis,sign:Number(button.dataset.sign)};
  };
  button.onpointerup=button.onpointercancel=button.onlostpointercapture=()=>{if(jog){jog=null;deadmanOff();}};
}
window.addEventListener('blur',()=>{jog=null;deadmanOff();});
document.addEventListener('visibilitychange',()=>{if(document.hidden){jog=null;deadmanOff();}});
window.addEventListener('pagehide',disconnect);
setInterval(()=>{
  const age=performance.now()-arrived;
  $('age').textContent=telemetry?`Возраст пакета ${Math.round(age)} мс`:'Нет телеметрии';
  if(jog && age>200){jog=null;send('stop');}
  if(jog && telemetry?.tool && !telemetry.fault && leaseId){
    const pose=structuredClone(telemetry.tool);pose.position[jog.axis]+=jog.sign*.002;
    send('command',{sequence:++sequence,observedSequence:telemetry.sequence,deadman:true,pose,gripper:Number($('gripper').value)});
  }
},40);
setInterval(()=>{if(leaseId)send('heartbeat',{},m=>{if(!m.ok){leaseId='';jog=null;}});for(const[key,p]of pending)if(performance.now()>p.expires)pending.delete(key);},250);
async function api(route){const response=await fetch(route,{headers:{authorization:`Bearer ${$('token').value}`}});if(!response.ok)throw new Error((await response.json()).error);return response;}
async function refresh(){try{
  const [status,files]=await Promise.all([(await api('/api/status')).json(),(await api('/api/recordings')).json()]);
  $('integrations').textContent=`Сервер: ${status.tickMs} мс / цикл; расчёт ${status.loopMs.toFixed(2)} мс\nOllama: ${status.integrations.ollama.status}\nМодель: ${status.integrations.ollama.model||'не выбрана'}\nSiemens: ${status.integrations.siemens.status}`;
  const previous=$('recordings').value;
  $('recordings').replaceChildren(...files.map(f=>new Option(`${f.id}${f.active?' · запись идёт':''}`,f.id)));
  if(files.some(f=>f.id===previous))$('recordings').value=previous;
}catch(e){log(e.message);}}
$('refresh').onclick=refresh;
$('record').onclick=()=>send(telemetry?.recording?.active?'recording.stop':'recording.start',{},()=>refresh());
$('download').onclick=async()=>{try{if(!$('recordings').value)return;const blob=await(await api('/api/recordings/'+$('recordings').value)).blob();const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=$('recordings').value+'.jsonl';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}catch(e){log(e.message);}};
$('validate').onclick=()=>{if(!$('recordings').value)return;$('validation').textContent='Запрос проверки…';send('validation.request',{recordingId:$('recordings').value},m=>{if(!m.ok)$('validation').textContent=m.error;});};
$('ask').onclick=()=>{$('advice').textContent='Ожидание ответа модели…';send('ai.request',{prompt:$('prompt').value},m=>{if(!m.ok)$('advice').textContent=m.error;});};
function show(t){
  const p=t.tool?.position;$('tcp').textContent=p?`X ${p.x.toFixed(3)}  ·  Y ${p.y.toFixed(3)}  ·  Z ${p.z.toFixed(3)}`:'Нет обратной связи';
  $('joints').textContent=t.joints?.length?t.joints.map((q,i)=>`${i+1}: ${(q*180/Math.PI).toFixed(1)}°`).join('  '):'Не получены';
  $('owner').textContent=t.leaseOwner||'Свободен';$('mode').textContent=t.mode==='simulation'?'СИМУЛЯЦИЯ':'ROS / ФИЗИЧЕСКИЙ';
  $('state').textContent=t.fault?`Остановка: ${t.fault}`:t.motionKnown===false?'Фактическое движение: нет измеренного статуса':t.moving?'Движение':t.stopReason||'Готов';
  $('recordState').textContent=t.recording.active?`Запись · ${t.recording.samples} отсчётов`:'Запись выключена';
  $('record').textContent=t.recording.active?'Завершить запись':'Начать запись';
  for(const el of document.querySelectorAll('[data-axis],#record,#release,#reset'))el.disabled=!leaseId;
  $('acquire').disabled=!!leaseId||!!t.leaseOwner;
  draw();
}
const canvas=$('view'),ctx=canvas.getContext('2d');let drag;
canvas.onpointerdown=e=>{drag=e.clientX;canvas.setPointerCapture(e.pointerId);};canvas.onpointermove=e=>{if(drag!==undefined){yaw+=(e.clientX-drag)*.01;drag=e.clientX;draw();}};canvas.onpointerup=canvas.onpointercancel=()=>{drag=undefined;};
function draw(){
  const dpr=devicePixelRatio||1,w=canvas.clientWidth,h=canvas.clientHeight;canvas.width=w*dpr;canvas.height=h*dpr;ctx.scale(dpr,dpr);ctx.clearRect(0,0,w,h);
  const scale=Math.min(w/2.8,h/2),project=p=>{const x=p.x*Math.cos(yaw)-p.z*Math.sin(yaw),z=p.x*Math.sin(yaw)+p.z*Math.cos(yaw);return[w*.4+x*scale,h*.78-p.y*scale+z*scale*.25];};
  const line=(a,b,color,width)=>{ctx.beginPath();ctx.moveTo(...project(a));ctx.lineTo(...project(b));ctx.strokeStyle=color;ctx.lineWidth=width;ctx.lineCap='round';ctx.stroke();};
  for(let x=-1.2;x<=1.21;x+=.2){line({x,y:0,z:-1.2},{x,y:0,z:1.2},'#21303c',1);line({x:-1.2,y:0,z:x},{x:1.2,y:0,z:x},'#21303c',1);}
  const links=telemetry?.links||[];
  for(let i=1;i<links.length;i++){line(links[i-1],links[i],'#85be52',14-i);const[x,y]=project(links[i]);ctx.beginPath();ctx.arc(x,y,5,0,Math.PI*2);ctx.fillStyle='#d9e8da';ctx.fill();}
  if(!links.length){ctx.fillStyle='#7f94a6';ctx.font='13px Segoe UI';ctx.fillText('Геометрия появится после получения телеметрии',20,40);}
  if(telemetry?.tool?.position){const[x,y]=project(telemetry.tool.position);ctx.fillStyle='#b6f367';ctx.font='11px Segoe UI';ctx.fillText('TCP',x+12,y-10);}
}
new ResizeObserver(draw).observe(canvas);draw();
