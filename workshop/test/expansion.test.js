import test from 'node:test';
import assert from 'node:assert/strict';
import {EventEmitter} from 'node:events';
import {mkdtemp,rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {KinematicRobot,defaultRobotConfig,forwardKinematics} from '../server/robot/index.js';
import {checkConfiguration,segmentDistanceSquared} from '../server/robot/collision.js';
import {WorkshopController} from '../server/controller.js';
import {loadConfig} from '../server/config.js';

test('capsule model detects folded self collision, including degenerate distance cases',()=>{
  assert.equal(segmentDistanceSquared([0,0,0],[1,0,0],[.5,-1,0],[.5,1,0]),0);
  assert.equal(segmentDistanceSquared([0,0,0],[0,0,0],[0,2,0],[1,2,0]),4);
  const config=structuredClone(defaultRobotConfig);config.obstacles=[];config.workspace={min:{x:-5,y:-5,z:-5},max:{x:5,y:5,z:5}};
  assert.equal(checkConfiguration(config,[0,-2,-2.6,0,-2.4,0]).code,'SELF_COLLISION');
  assert.equal(checkConfiguration(config,config.home).ok,true);
});
test('load is bounded, reduces travel speed, and deadman release keeps its immediate stop',()=>{
  const free=new KinematicRobot(),loaded=new KinematicRobot();
  assert.equal(loaded.setPayload(6).ok,false);assert.equal(loaded.setPayload(NaN).ok,false);assert.equal(loaded.setPayload(5).ok,true);
  const q=[.16,.98,-1.3,.2,.4,-.1],pose=forwardKinematics(free.config,q);
  for(const r of [free,loaded]){r.setDeadman(true);assert.equal(r.command(pose,.1).ok,true);for(let i=0;i<10;i++)assert.equal(r.tick(.02).ok,true);}
  assert.ok(Math.abs(loaded.joints[0]-loaded.config.home[0])<Math.abs(free.joints[0]-free.config.home[0]));
  loaded.setDeadman(false);const before=loaded.snapshot();loaded.tick(.02);assert.deepEqual(loaded.snapshot().joints,before.joints);
});
test('read-only diagnostics report lag and integrations; cabinet context reaches advisor without control commands',async t=>{
  const directory=await mkdtemp(path.join(os.tmpdir(),'vr-expansion-'));
  const config=await loadConfig({HOST:'127.0.0.1',PORT:'0',DATA_DIR:directory,VR_ACCESS_TOKEN:'fixture-token'});
  let time=1000,context;
  const controller=new WorkshopController(config,{now:()=>time,advisor:{snapshot:()=>({status:'TEST'}),advise:async s=>{context=s;return {ok:true,source:'fixture',summary:'test'};}}});
  t.after(async()=>{await controller.close();assert.equal(path.dirname(directory),path.resolve(os.tmpdir()));assert.ok(path.basename(directory).startsWith('vr-expansion-'));await rm(directory,{recursive:true,force:true});});
  class Socket extends EventEmitter{readyState=1;bufferedAmount=0;messages=[];send(text,cb){this.messages.push(JSON.parse(text));cb?.();}close(){this.readyState=3;this.emit('close',1000);}}
  const socket=new Socket(),client=controller.attach(socket);
  await controller.handle(client,{type:'auth',id:'a',protocolVersion:1,accessToken:'fixture-token'});
  time+=100;controller.tick();
  await controller.handle(client,{type:'ping',id:'p'});
  assert.equal(socket.messages.at(-1).diagnostics.lateTicks,1);assert.equal(socket.messages.at(-1).diagnostics.maxTickGapMs,100);
  assert.equal(socket.messages.at(-1).integrations.amas.status,'NOT_CONFIGURED');
  await controller.handle(client,{type:'ai.request',id:'ai',robotId:'training-arm',prompt:'Шкаф',cabinetContext:JSON.stringify({mode:'Практика',mounted:4,next:'Установить модуль',execute:'move'})});
  assert.equal(context.cabinet.mounted,4);assert.equal(context.cabinet.clientReported,true);assert.equal(context.cabinet.execute,undefined);
  await controller.handle(client,{type:'ai.request',id:'exam',robotId:'training-arm',prompt:'Как собрать шкаф?',cabinetContext:JSON.stringify({mode:'Экзамен',next:'Secret wire order'})});
  assert.equal(socket.messages.at(-1).source,'rules');assert.ok(!socket.messages.at(-1).summary.includes('Secret'));
  assert.ok([...controller.robots.values()].every(r=>r.lease===null&&!r.deadman));
});
