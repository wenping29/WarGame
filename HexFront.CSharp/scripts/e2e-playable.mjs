// 端到端：创建副本 → 双端连房间 → 提交命令 → 回合结算 → 存档 → 存档回放
const BASE='ws://localhost:5018/ws', API='http://localhost:5018';
const log=(...a)=>console.log(...a); const fail=m=>{console.error('FAIL:',m);process.exit(1);};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
function connect(qs){return new Promise((resolve,reject)=>{const ws=new WebSocket(BASE+qs);const msgs=[];const cnt={};const waits=[];
  const pump=()=>{for(const w of[...waits])if((cnt[w.type]||0)>=w.need){waits.splice(waits.indexOf(w),1);clearTimeout(w.timer);w.res(msgs.filter(m=>m.type===w.type).slice(-w.count));}};
  ws.onmessage=e=>{let m;try{m=JSON.parse(e.data);}catch{return;}msgs.push(m);cnt[m.type]=(cnt[m.type]||0)+1;pump();};
  ws.onopen=()=>resolve({ws,msgs,send:o=>ws.send(JSON.stringify(o)),
    waitFor:(type,count=1,ms=8000)=>new Promise((res,rej)=>{if((cnt[type]||0)>=count){res(msgs.filter(m=>m.type===type).slice(-count));return;}
      const w={type,count,need:count,res};w.timer=setTimeout(()=>{waits.splice(waits.indexOf(w),1);rej(new Error('timeout '+type+' x'+count));},ms);waits.push(w);}),
    last:type=>msgs.filter(m=>m.type===type).pop()});
  ws.onerror=e=>reject(new Error('ws error'));});}

// 1. 创建副本（REST）
const inst=await (await fetch(API+'/api/instances',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({scenario:'rhine_corridor',name:'端到端副本'})})).json();
if(!inst.code) fail('create instance failed: '+JSON.stringify(inst));
const CODE=inst.code; log('1. 副本创建成功 code='+CODE+' instance='+inst.instance);

// 2. 导演+双端连房间
const dir=await connect(`?room=${CODE}&role=director&name=dir`);
await dir.waitFor('welcome'); const [d0]=await dir.waitFor("state");
const units=d0.view.units||[]; if(units.length<30) fail('director sees only '+units.length+' units (expected ~36)');
if(!d0.view.orders||!d0.view.orders.red||!d0.view.last_errors||!d0.view.ai) fail('director view missing orders/last_errors/ai');
if(!Array.isArray(d0.view.cells)||!d0.view.cells.length||!d0.view.weather||!d0.view.day||!d0.view.geo) fail('director view missing cells/weather/day/geo');
log('2. 导演连房，全知单位数='+units.length+' 起始回合='+d0.turn);
const red=await connect(`?room=${CODE}&role=red&name=redterm`); await red.waitFor('welcome');
const blue=await connect(`?room=${CODE}&role=blue&name=bluetrm`); await blue.waitFor('welcome');
const [rs]=await red.waitFor("state"), [bs]=await blue.waitFor("state");
const rOwn=rs.view.own||[], bOwn=bs.view.own||[];
if(!rOwn.length||!bOwn.length) fail('side own lists empty r='+rOwn.length+' b='+bOwn.length);
for (const v of [rs.view, bs.view]) {
  if(!Array.isArray(v.cells)||!v.cells.length) fail('side view missing cells');
  if(!v.weather||!v.day||!v.geo||v.width==null||v.hour==null) fail('side view missing weather/day/geo/width/hour');
  if(!Array.isArray(v.contacts)||!Array.isArray(v.errors)) fail('side view missing contacts/errors');
}
const u0=rOwn[0];
if(u0.type_label==null||u0.morale==null||u0.status==null||u0.q==null||u0.equipment==null) fail('own unit serialization missing fields');
log('   红方本方'+rOwn.length+' 蓝方本方'+bOwn.length+'（迷雾视图，contacts 红'+rs.view.contacts.length+'/蓝'+bs.view.contacts.length+'）');

// 3. 双端各提交移动/休整命令
const mover=rOwn.find(u=>u.mobility>0)||rOwn[0];
red.send({type:'orders',replace:true,commands:[{op:'rest',unit:mover.id}]});
const bMover=bOwn.find(u=>u.mobility>0)||bOwn[0];
blue.send({type:'orders',replace:true,commands:[{op:'rest',unit:bMover.id}]});
await sleep(400);
const rp=red.last('state'); if(rp.pending.red<1) fail('red pending order not registered');
log('3. 双端命令已登记 pending='+JSON.stringify(rp.pending));

// 4. 双方就绪 + 导演结算
red.send({type:'ready',value:true}); blue.send({type:'ready',value:true});
await sleep(200);
const evBefore=dir.msgs.filter(m=>m.type==='events').length;
dir.send({type:'resolve'});
await dir.waitFor('events',evBefore+1,15000);
const stBefore=dir.msgs.filter(m=>m.type==='state').length;
const sArr2=await dir.waitFor('state',stBefore+1,8000); const after=sArr2[sArr2.length-1];
if(after.turn!==d0.turn+1) fail('turn expected '+(d0.turn+1)+' got '+after.turn);
await sleep(400);
const redEv=red.msgs.filter(m=>m.type==='events').flatMap(m=>m.events||[]);
if(redEv.some(e=>!e.sides.includes('red'))) fail('event leak: red received event not involving red');
log('4. 回合结算：turn '+d0.turn+'→'+after.turn+' 事件数='+dir.msgs.filter(m=>m.type==='events').flatMap(m=>m.events).length+' 红方可见事件='+redEv.length+'（过滤后无泄漏）');

// 5. 存档
dir.send({type:'save',name:'e2e存档'});
const [saved]=await dir.waitFor("saved",1,8000);
log('5. 存档 id='+saved.instance+' turn='+saved.turn+' size='+saved.meta.file_size);

// 6. 回放帧
const fr=await (await fetch(API+'/api/frames/'+CODE)).json();
if(!fr.frames.length) fail('no frames after resolve');
const f=fr.frames[fr.frames.length-1];
if(!f.director) fail('frame missing director view');
log('6. 回放帧数='+fr.frames.length+' 末帧 turn='+f.turn+' 含 director 全知视图，单位'+f.director.units.length);

// 7. 存档载入 → 新房间可回放
const load=await (await fetch(API+'/api/saves/load',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({instance:saved.instance})})).json();
if(!load.code) fail('saves/load failed');
const dir2=await connect(`?room=${load.code}&role=director&name=replay`);
await dir2.waitFor('welcome'); const [d2]=await dir2.waitFor("state");
log('7. 存档回放：新房间 '+load.code+' resumed_turn='+load.resumed_turn+' 当前turn='+d2.turn+' 单位'+d2.view.units.length);
// 请求历史帧回放（存档模式：init 帧）
dir2.send({type:'frames'}); const f2=await dir2.waitFor('frames',1,8000);
log('   复盘帧='+f2[0].frames.length);

[dir,red,blue,dir2].forEach(c=>{try{c.ws.close();}catch{}});
log('\n端到端全部通过 ✅ 创建→连房→命令→结算→存档→回放');
process.exit(0);
