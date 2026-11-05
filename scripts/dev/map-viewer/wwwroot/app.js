const $ = s => document.querySelector(s);
const canvas = $('#map'), ctx = canvas.getContext('2d'), viewport = $('#viewport');
let map, tab = 'tiles', selected = '', zoom = 1, panX = 0, panY = 0, dragging = null, moved = false, image;
const short = p => (p || '').split(/[\\/]/).pop();
const bossName = p => { const names=(p.bosses||[]).map(b=>b.name||b.id).filter(Boolean).join(' / '); return names ? (p.bossesAreCandidates ? `可能的首领：${names}` : names) : p.name; };
const text = (el, value) => { el.textContent = value; };
fetch('/api/areas').then(r => r.json()).then(a => { for (const x of a) { const o = document.createElement('option'); o.value = x.id; o.label = x.name; $('#areas').append(o); } });
$('#form').onsubmit = async e => {
  e.preventDefault(); text($('#state'), '正在生成...');
  const q = new URLSearchParams({ id: $('#area').value, seed: $('#seed').value });
  if ($('#level').value) q.set('level', $('#level').value);
  try {
    const r = await fetch('/api/map?' + q), data = await r.json(); if (!r.ok) throw Error(data.error);
    map = data; selected = ''; text($('#title'), data.area); text($('#subtitle'), `${data.id} · 种子 ${data.seed} · 等级 ${data.level}`);
    const values = Uint8Array.from(atob(data.walk), c => c.charCodeAt(0));
    image = document.createElement('canvas'); image.width = data.walkWidth; image.height = data.walkHeight;
    const ic = image.getContext('2d'), pixels = ic.createImageData(image.width, image.height);
    for (let i = 0; i < values.length; i++) { const c = values[i] >= 2 ? [155,189,161] : [69,84,76]; pixels.data.set([...c,255], i*4); }
    ic.putImageData(pixels, 0, 0); $('#empty').style.display = 'none'; text($('#state'), data.warnings.length ? '通行数据不完整' : '已生成'); if(data.warnings.length) text($('#detail'),data.warnings.join('；')); renderList(); fit();
  } catch (err) { text($('#state'), '生成失败'); text($('#detail'), err.message); }
};
function renderList() {
  if (!map) return; const list = $('#list'); list.replaceChildren(); const filter = $('#filter').value.toLowerCase();
  const entries = tab === 'tiles' ? map.catalog.map(x => ({ key:x.path, title:short(x.path), sub:`${x.path} · 已放置 ${x.count} 次` })) : [
    ...map.exits.map((x,i) => ({ key:`exit:${i}`, title:`${x.kind} ${x.number} ${x.destination || x.label || ''}`, sub:`(${x.x}, ${x.y}) ${x.enabled ? '' : '未启用'} ${x.why}` })),
    ...map.entrances.map((x,i) => ({ key:`entrance:${i}`, title:`到达点 ${x.number}`, sub:`(${x.x}, ${x.y}) ${x.enabled ? '' : '仅到达'}` })),
    ...map.objects.map((x,i) => ({ key:`object:${i}`, title:short(x.type), sub:`(${x.x}, ${x.y}) ${x.type}` })),
    ...map.points.map((x,i) => ({ key:`point:${i}`, title:bossName(x), sub:`(${x.x}, ${x.y}) ${x.name} ${(x.bosses||[]).map(b=>b.id).join(' / ')} ${x.room}` }))];
  const shown = entries.filter(x => (x.title + x.sub).toLowerCase().includes(filter)); text($('#count'), shown.length + ' 项');
  for (const entry of shown) { const d = document.createElement('div'); d.className = 'item' + (selected === entry.key ? ' active':''); const a = document.createElement('strong'), b = document.createElement('small'); text(a,entry.title); text(b,entry.sub); d.append(a,b); d.onclick = () => { selected = entry.key; text($('#detail'), entry.sub); renderList(); draw(); }; list.append(d); }
}
document.querySelectorAll('.tabs button').forEach(b => b.onclick = () => { tab=b.dataset.tab; document.querySelectorAll('.tabs button').forEach(x=>x.classList.toggle('active',x===b)); renderList(); });
$('#filter').oninput=renderList; ['walk','grid','markers'].forEach(id => $('#'+id).onchange=draw);
function resize(){ const d=window.devicePixelRatio||1; canvas.width=Math.round(viewport.clientWidth*d); canvas.height=Math.round(viewport.clientHeight*d); ctx.setTransform(d,0,0,d,0,0); draw(); }
function fit(){ if(!map)return; const size=(map.width+map.height)/Math.SQRT2; zoom=Math.min((viewport.clientWidth-40)/size,(viewport.clientHeight-40)/size); panX=viewport.clientWidth/2+(map.height-map.width)*zoom/(2*Math.SQRT2); panY=(viewport.clientHeight+size*zoom)/2; draw(); }
$('#fit').onclick=fit; window.addEventListener('resize',resize);
function draw(){ const w=viewport.clientWidth,h=viewport.clientHeight; ctx.clearRect(0,0,w,h); if(!map)return; ctx.save(); ctx.translate(panX,panY); ctx.scale(zoom,zoom); ctx.rotate(-Math.PI/2); ctx.scale(1,-1); ctx.rotate(-Math.PI/4); ctx.fillStyle='#45544c'; ctx.fillRect(0,0,map.width,map.height); if($('#walk').checked){ctx.imageSmoothingEnabled=false;ctx.drawImage(image,0,0,map.width,map.height);} if($('#grid').checked){ctx.strokeStyle='rgba(20,30,26,.35)';ctx.lineWidth=1/zoom;ctx.beginPath();for(let x=0;x<=map.width;x++){ctx.moveTo(x,0);ctx.lineTo(x,map.height);}for(let y=0;y<=map.height;y++){ctx.moveTo(0,y);ctx.lineTo(map.width,y);}ctx.stroke();} if(selected&&!selected.includes(':')){ctx.fillStyle='rgba(255,203,103,.55)'; for(const t of map.tiles)if(t.path===selected)ctx.fillRect(t.x,t.y,1,1);} if($('#markers').checked){const mark=(x,y,c)=>{ctx.beginPath();ctx.arc(x/23,y/23,Math.max(0.28,4/zoom),0,7);ctx.fillStyle=c;ctx.fill();ctx.strokeStyle='#172422';ctx.lineWidth=1/zoom;ctx.stroke();};map.objects.forEach(o=>mark(o.x,o.y,'#e98379'));map.points.forEach(o=>mark(o.x,o.y,'#e98379'));map.entrances.forEach(o=>mark(o.x,o.y,'#f3ba54'));map.exits.forEach(o=>mark(o.x,o.y,'#f3ba54'));}ctx.restore(); }
canvas.onwheel=e=>{e.preventDefault();if(!map)return;const rect=canvas.getBoundingClientRect(),x=e.clientX-rect.left,y=e.clientY-rect.top,old=zoom;zoom=Math.max(.5,Math.min(100,zoom*Math.exp(-e.deltaY*.001)));panX=x-(x-panX)*zoom/old;panY=y-(y-panY)*zoom/old;draw();};
canvas.onpointerdown=e=>{dragging={x:e.clientX,y:e.clientY,px:panX,py:panY};moved=false;canvas.setPointerCapture(e.pointerId);canvas.classList.add('dragging');};
canvas.onpointermove=e=>{if(!dragging)return;const dx=e.clientX-dragging.x,dy=e.clientY-dragging.y;if(Math.abs(dx)+Math.abs(dy)>3)moved=true;panX=dragging.px+dx;panY=dragging.py+dy;draw();};
canvas.onpointerup=e=>{canvas.classList.remove('dragging');if(!moved&&map){const r=canvas.getBoundingClientRect(),dx=(e.clientX-r.left-panX)/zoom,dy=(e.clientY-r.top-panY)/zoom,x=(dx-dy)/Math.SQRT2,y=(-dx-dy)/Math.SQRT2;const markers=[...map.exits.map((o,i)=>({...o,key:`exit:${i}`,label:`${o.kind} ${o.number} ${o.destination||o.label||''}`})),...map.entrances.map((o,i)=>({...o,key:`entrance:${i}`,label:`到达点 ${o.number}`})),...map.objects.map((o,i)=>({...o,key:`object:${i}`,label:o.type})),...map.points.map((o,i)=>({...o,key:`point:${i}`,label:bossName(o)}))];const marker=$('#markers').checked?markers.find(o=>Math.hypot(o.x/23-x,o.y/23-y)<Math.max(.4,7/zoom)):null;const tile=map.tiles.find(t=>t.x===Math.floor(x)&&t.y===Math.floor(y));if(marker){selected=marker.key;tab='objects';text($('#detail'),`${marker.label} · (${marker.x}, ${marker.y})`);}else if(tile){selected=tile.path;tab='tiles';text($('#detail'),`${tile.path} · (${tile.x}, ${tile.y}) · 朝向 ${tile.orientation} · 高度 ${tile.height}`);}document.querySelectorAll('.tabs button').forEach(b=>b.classList.toggle('active',b.dataset.tab===tab));renderList();draw();}dragging=null;};
resize();
