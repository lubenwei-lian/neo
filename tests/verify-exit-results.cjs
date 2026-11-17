const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const root=path.resolve(__dirname,'../src/Neo.App/Assets/HtmlShell/js');
async function run(file){
  const source=fs.readFileSync(path.join(root,file),'utf8');
  const start=source.indexOf('  var nativeExitStorage='),end=source.indexOf('  function nativeOpenExitTest()',start);
  assert(start>=0&&end>start);
  const notices=[],requests=[],stored={};
  const c=vm.createContext({nativeSnapshot:null,NODES:[{id:'jp',name:'Japan',region:'日本'},{id:'jp',name:'Duplicate',region:'日本'},{id:'sg',name:'Singapore',region:'新加坡'}],Set,Map,Date,Number,Object,JSON,Promise,
    localStorage:{getItem:()=>JSON.stringify(stored),setItem:(_,v)=>Object.assign(stored,JSON.parse(v))},toast:s=>notices.push(s)});
  vm.runInContext(source.slice(start,end),c);
  vm.runInContext('nativePaintExitMarks=function(){};nativePaintTestControls=function(){};',c);
  c.host=async(_,p)=>{requests.push(p.keys);return [{key:'sg',ip:'8.8.8.8',country:'新加坡',confirmed:true,retryable:false},{key:'jp',ip:'1.1.1.1',country:'日本',confirmed:true,retryable:false}];};
  await vm.runInContext('nativeRunExitTests(true)',c);
  assert.deepEqual(Array.from(requests[0]),['jp','sg']);
  assert.equal(c.nativeExitHistory.jp.country,'日本');assert.equal(c.nativeExitHistory.sg.country,'新加坡');
  assert.equal(notices.length,0);assert.equal(c.nativeExitPending.size,0);assert.equal(c.nativeExitRunning,false);
  await vm.runInContext('nativeRunExitTests(false)',c);assert.equal(requests.length,1,'confirmed nodes rechecked');
  c.host=async()=>[{key:'jp',confirmed:false,retryable:true,detail:'request timeout'},{key:'sg',confirmed:false,retryable:false,detail:'不支持'}];
  await vm.runInContext('nativeRunExitTests(true)',c);
  let retryKeys;c.host=async(_,p)=>{retryKeys=p.keys;return [{key:'jp',country:'日本',ip:'1.1.1.1',confirmed:true,retryable:false}];};
  await vm.runInContext('nativeRunExitTests(false)',c);assert.deepEqual(Array.from(retryKeys),['jp']);assert.equal(c.nativeExitHistory.jp.confirmed,true);
  const saved=JSON.stringify(c.nativeExitHistory);
  c.host=async()=>[{key:'sg',confirmed:true}];await vm.runInContext('nativeRunExitTests(true)',c);
  assert.equal(JSON.stringify(c.nativeExitHistory),saved,'partial response corrupted earlier results');
  assert.equal(notices.length,1);assert.equal(c.nativeExitRunning,false);assert.equal(c.nativeExitPending.size,0);
  const legacy=vm.createContext({...c,localStorage:{getItem:()=>JSON.stringify({jp:{at:1,confirmed:false,country:'',ip:'',detail:'未确认'},sg:{at:1,confirmed:true,country:'新加坡',ip:'8.8.8.8'}})}});
  vm.runInContext(source.slice(start,end),legacy);
  assert.equal(vm.runInContext("nativeExitNeedsTest({id:'jp'})",legacy),true);assert.equal(vm.runInContext("nativeExitNeedsTest({id:'sg'})",legacy),false);
  const migrated=vm.createContext({...c,localStorage:{getItem:key=>key==='neo.exit-regions.v1'?JSON.stringify({jp:{at:1,confirmed:false,country:'',ip:'',detail:'不支持',retryable:false},sg:{at:1,confirmed:true,country:'新加坡',ip:'8.8.8.8'}}):null}});
  vm.runInContext(source.slice(start,end),migrated);
  assert.equal(vm.runInContext("nativeExitNeedsTest({id:'jp'})",migrated),true,'old unsupported cache blocks newly supported protocol');
  assert.equal(vm.runInContext("nativeExitNeedsTest({id:'sg'})",migrated),false,'migration discards successful measurements');
  console.log('PASS '+file+': duplicate configurations, keyed ordering, retry, cache reuse, partial results, legacy failures');
}
(async()=>{await run('native-view.js');await run('app2.js');})().catch(e=>{console.error(e);process.exitCode=1;});
