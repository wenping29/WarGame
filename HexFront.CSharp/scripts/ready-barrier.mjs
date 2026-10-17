// 第2步专项：就绪自动结算屏障 / director_cmd 语义 / clear_orders / AI 托管补令
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
const waitTurn=async(c,t,ms=15000)=>{const end=Date.now()+ms;while(Date.now()<end){const s=c.last('state');if(s&&s.turn>=t)return s;await sleep(100);}fail('timeout waiting turn '+t);};
const evs=c=>c.msgs.filter(m=>m.type==='events').flatMap(m=>m.events||[]);

// 0. 建房 + 四端连入
const inst=await (await fetch(API+'/api/instances',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({scenario:'rhine_corridor',name:'屏障测试'})})).json();
if(!inst.code) fail('create failed '+JSON.stringify(inst));
const CODE=inst.code;
const dir=await connect(`?room=${CODE}&role=director`); await dir.waitFor('welcome');
const [d0]=await dir.waitFor('state'); const T0=d0.turn;
const red=await connect(`?room=${CODE}&role=red`); await red.waitFor('welcome'); await red.waitFor('state');
const blue=await connect(`?room=${CODE}&role=blue`); await blue.waitFor('welcome'); await blue.waitFor('state');
const spv=await connect(`?room=${CODE}&role=spectator`); await spv.waitFor('welcome'); await spv.waitFor('state');
log('0. 房间 '+CODE+' 连入4端 turn='+T0);

// 1. 非对抗方提交 orders 被拒
const ec=dir.msgs.filter(m=>m.type==='error').length;
dir.send({type:'orders',commands:[{op:'rest',unit:d0.view.units[0].id}]});
const a1=await dir.waitFor('error',ec+1); const e1=a1[a1.length-1];
if(!e1.message.includes('只有对抗方')) fail('orders role guard: '+e1.message);
log('1. 导演提交 orders 被拒: '+e1.message);

// 2. 草案含非法指令 → ready 被拒且保持未就绪
red.send({type:'orders',replace:true,commands:[{op:'attack',unit:'ghost_u',target:'ghost_v'}]});
await sleep(200);
const rec=red.msgs.filter(m=>m.type==='error').length;
red.send({type:'ready',value:true});
const a2=await red.waitFor('error',rec+1); const e2=a2[a2.length-1];
if(!e2.message.includes('非法指令')) fail('ready validation: '+e2.message);
if(red.last('state').ready.red!==false) fail('ready should stay false after invalid');
log('2. 非法草案下就绪被拒: '+e2.message);

// 3. clear_orders 清空草案并撤销就绪
red.send({type:'clear_orders'});
await sleep(200);
const rs=red.last('state');
if((rs.view.draft||[]).length!==0||rs.draft.length!==0) fail('clear_orders left draft non-empty');
log('3. clear_orders 后草案清空 draft=0');

// 4. 空草案就绪成功；蓝方未就绪 → 屏障不触发
red.send({type:'ready',value:true});
await sleep(700);
if(red.last('state').ready.red!==true) fail('red ready true expected');
if(dir.last('state').turn!==T0) fail('barrier fired prematurely!');
log('4. 红方就绪、蓝方未就绪 → 回合保持 '+dir.last('state').turn+'（屏障未误触发）');

// 5. 非导演导调被拒
const bec=blue.msgs.filter(m=>m.type==='error').length;
blue.send({type:'director_cmd',action:'fog',on:false});
const a5=await blue.waitFor('error',bec+1); const e5=a5[a5.length-1];
if(!e5.message.includes('只有导演部')) fail('director_cmd guard: '+e5.message);
log('5. 蓝方导调被拒: '+e5.message);

// 6. spectator_omniscient on/off（Dart 语义：on 参数）
dir.send({type:'director_cmd',action:'spectator_omniscient',on:true});
await sleep(300);
if(spv.last('state').view.kind!=='director') fail('omniscient spectator should get director view');
dir.send({type:'director_cmd',action:'spectator_omniscient',on:false});
await sleep(300);
if(spv.last('state').view.kind!=='spectator') fail('spectator view should revert');
log('6. spectator_omniscient on→kind=director, off→kind=spectator');

// 7. 改天气：weather_set 事件三方可见；非法 id 报错
const redEvBefore=evs(red).length;
dir.send({type:'director_cmd',action:'weather',id:'rain'});
await sleep(400);
const w=dir.last('state').view.weather;
if(w.id!=='rain') fail('weather not applied: '+JSON.stringify(w));
const wsEv=evs(red).slice(redEvBefore).find(e=>e.code==='weather_set');
if(!wsEv) fail('red missed weather_set event');
const nec=dir.msgs.filter(m=>m.type==='error').length;
dir.send({type:'director_cmd',action:'weather',id:'blizzard9'});
const a7=await dir.waitFor('error',nec+1); const e7=a7[a7.length-1];
if(!e7.message.includes('未知天气')) fail('bad weather: '+e7.message);
log('7. 天气设为 rain（weather_set 已广播），非法天气被拒: '+e7.message);

// 8. 迷雾开关：fog 事件仅导演可见
dir.send({type:'director_cmd',action:'fog',on:false});
await sleep(400);
const fogEvDir=evs(dir).filter(e=>e.code==='fog');
const fogEvRed=evs(red).filter(e=>e.code==='fog');
if(!fogEvDir.length) fail('director missed fog event');
if(fogEvRed.length) fail('fog event leaked to red');
if(dir.last('state').view.fog!==false) fail('flags.fog should be false');
dir.send({type:'director_cmd',action:'fog',on:true});
await sleep(300);
log('8. 迷雾事件仅导演可见（红方0泄漏），flags 生效');

// 9. inject_intel：按 unit id 暴露给指定方（level3 接触）
const blueUnit=dir.last('state').view.units.filter(u=>u.side==='blue'&&u.status!=='destroyed')[0];
dir.send({type:'director_cmd',action:'inject_intel',side:'red',unit:blueUnit.id});
await sleep(400);
const contacts=red.last('state').view.contacts||[];
const c0=contacts.find(x=>x.id===blueUnit.id);
if(!c0||c0.level!==3) fail('injected contact missing/level!=3: '+JSON.stringify(c0));
if(c0.personnel==null||c0.equipment==null) fail('level3 contact missing detail fields');
const intelLeak=evs(blue).filter(e=>e.code==='intel');
if(intelLeak.length) fail('intel event leaked to blue');
log('9. inject_intel '+blueUnit.id+' → 红方 level3 接触含人数/装备，蓝方无泄漏');

// 10. AI 代管即视为就绪 → 屏障自动结算（红方已就绪，开蓝方 AI 即触发）
dir.send({type:'director_cmd',action:'ai',side:'blue',on:true});
const after=await waitTurn(dir,T0+1);
if(after.view.ai.blue!==true) fail('ai.blue flag not surfaced in director view');
if(red.last('state').view.ai_managed!==false) fail('red side view ai_managed should be false');
if(blue.last('state').view.ai_managed!==true) fail('blue side view ai_managed!=true');
log('10. 蓝方 AI 代管开启 → 屏障自动结算 turn '+T0+'→'+after.turn+'（byRole=director-ai）');

// 11. jump_turn：越界报错；合法 no-op 不推进回合
const jac=dir.msgs.filter(m=>m.type==='error').length;
dir.send({type:'director_cmd',action:'jump_turn',turn:9999});
const a11=await dir.waitFor('error',jac+1); const e11=a11[a11.length-1];
if(!e11.message.includes('只能回跳')) fail('jump_turn guard: '+e11.message);
const turnNow=dir.last('state').turn;
dir.send({type:'director_cmd',action:'jump_turn',turn:1});
await sleep(400);
if(dir.last('state').turn!==turnNow) fail('jump_turn should be no-op');
log('11. jump_turn 越界被拒、合法值 no-op: '+e11.message);

// 12. 双人类就绪 → 第二次自动结算；orders 使就绪回退
dir.send({type:'director_cmd',action:'ai',side:'blue',on:false});
await sleep(300);
red.send({type:'ready',value:true}); await sleep(200);
blue.send({type:'ready',value:true});
const after2=await waitTurn(dir,T0+2);
log('12. 双方就绪 → 自动结算 turn→'+after2.turn);
// 结算后就绪复位；重新就绪再撤单验证回退
red.send({type:'ready',value:true}); await sleep(250);
red.send({type:'orders',replace:true,commands:[{op:'rest',unit:red.last('state').view.own[0].id}]});
await sleep(250);
if(red.last('state').ready.red!==false) fail('new orders should cancel ready');
log('13. 新指令自动撤销就绪态 ✓');

[dir,red,blue,spv].forEach(c=>{try{c.ws.close();}catch{}});
log('\n第2步专项全部通过 ✅ 屏障/导调语义/clear_orders/AI 代管');
process.exit(0);
