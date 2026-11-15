// Rasterize the original SVG for WPF and the Windows icon. No logo redrawing.
import {spawn} from 'node:child_process';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const profile = path.join(root, 'bin', 'logo-chrome-' + process.pid);
await mkdir(profile, {recursive:true});
const child = spawn(process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe', ['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'], {windowsHide:true,stdio:'ignore'});
const sleep = ms => new Promise(r=>setTimeout(r,ms));
let socket;
try {
 let port;
 for(let i=0;i<100;i++){try{port=Number((await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]);break;}catch{await sleep(100);}}
 if(!port) throw Error('Chrome did not start');
 const pages=await(await fetch('http://localhost:'+port+'/json/list')).json();
 socket=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject;});
 let id=0; const pending=new Map();
 socket.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const cb=pending.get(m.id);pending.delete(m.id);m.error?cb.reject(Error(m.error.message)):cb.resolve(m.result);}};
 const call=(method,params={})=>new Promise((resolve,reject)=>{pending.set(++id,{resolve,reject});socket.send(JSON.stringify({id,method,params}));});
 await call('Emulation.setDeviceMetricsOverride',{width:256,height:256,deviceScaleFactor:1,mobile:false});
 await call('Emulation.setDefaultBackgroundColorOverride',{color:{r:0,g:0,b:0,a:0}});
 const svg=await readFile(path.join(root,'Assets','crayon-mark.svg'),'utf8');
 await call('Page.navigate',{url:'data:text/html;charset=utf-8,'+encodeURIComponent('<style>body{margin:0}svg{width:256px;height:256px}</style>'+svg)});
 await sleep(250);
 const shot=await call('Page.captureScreenshot',{format:'png'});
 const png=Buffer.from(shot.data,'base64');
 await writeFile(path.join(root,'Assets','crayon-mark.png'),png);
 const header=Buffer.alloc(22); header.writeUInt16LE(1,2);header.writeUInt16LE(1,4);header.writeUInt16LE(1,10);header.writeUInt16LE(32,12);header.writeUInt32LE(png.length,14);header.writeUInt32LE(22,18);
 await writeFile(path.join(root,'Assets','crayon.ico'),Buffer.concat([header,png]));
 await call('Browser.close');
} finally {socket?.close();child.kill();}
