// Walk-map capture (Frida, loaded by walkmaps.py into the running client).
//
// When the client generates an area it builds a terrain walkability grid before drawing it. Right after the grid
// provider returns (client 3.29.3b, VA 0x1410ddcfd, just after the vtable +0x50 call in 0x1410ddb90) rax points to
//   { i32 width; i32 height; vector<u8> cells [+0x08 begin, +0x10 end]; vector<u8> second [+0x20, +0x28] }.
// The first vector is width × height bytes, one per cell; the client walks on cells >= 2. The second, the same size,
// stops projectiles where it is 0 (walls, doodads; water is open there).
// Each grid is sent to walkmaps.py, which names it after the area the Gateway is serving.

const IMAGE_BASE = ptr('0x140000000');
const HOOK_VA = ptr('0x1410ddcfd');
// The instructions at the hook in the 3.29.3b client (mov rcx,[rax]; mov [rbp-0x38],rcx; lea rdx,[rax+8]; ...).
// Nothing is hooked unless they match, so another client build is left untouched.
const EXPECTED = '488b0848894dc8488d5008488d4dd048';

// The client executable: the .exe module (Process.mainModule can report a driver DLL when attaching late).
const exe = Process.enumerateModules().find(m => m.name.toLowerCase().endsWith('.exe'));
if (!exe) {
  send({ type: 'refused', reason: 'no .exe module in the process' });
} else {
  const hook = exe.base.add(HOOK_VA.sub(IMAGE_BASE));
  const inside = hook.compare(exe.base) >= 0 && hook.add(EXPECTED.length / 2).compare(exe.base.add(exe.size)) <= 0;
  const bytes = inside ? Array.from(new Uint8Array(hook.readByteArray(EXPECTED.length / 2)))
    .map(b => b.toString(16).padStart(2, '0')).join('') : '';
  if (bytes !== EXPECTED) {
    send({ type: 'refused', reason: `${exe.name} is not the 3.29.3b client (code at the hook does not match)` });
  } else {
    Interceptor.attach(hook, function () {
      try {
        const r = this.context.rax;
        const w = r.readS32(), h = r.add(4).readS32();
        const begin = r.add(0x08).readPointer(), end = r.add(0x10).readPointer();
        const n = end.sub(begin).toInt32();
        if (w <= 0 || h <= 0 || n !== w * h) {
          send({ type: 'skip', w, h, bytes: n });
          return;
        }
        send({ type: 'grid', w, h }, begin.readByteArray(n));
        // The second vector, same size: what stops projectiles (0 blocks; water and the like are 1..5 there).
        const b2 = r.add(0x20).readPointer(), n2 = r.add(0x28).readPointer().sub(b2).toInt32();
        if (n2 === w * h) send({ type: 'projectile', w, h }, b2.readByteArray(n2));
      } catch (e) {
        send({ type: 'error', message: String(e) });
      }
    });
    hookTileObjects(exe);
    hookDoodads(exe);
    hookRooms(exe);
    send({ type: 'ready', module: exe.name });
  }
}

// Placed .arm rooms, including named server-only spawn points absent from .tmd objects. The existing room-doodad
// loop visits every placed room even when it has no client doodads. Layout/addresses corroborated by PR #136's
// DoodadStage and SpecialNodes, then verified against the native reader below and the captured Strand rooms.
function hookRooms(exe) {
  const hook=exe.base.add(ptr('0x142040770').sub(IMAGE_BASE));
  const hex=Array.from(new Uint8Array(hook.readByteArray(16))).map(b=>b.toString(16).padStart(2,'0')).join('');
  if(hex!=='488bc44c894820488950104889480855') {send({type:'refused',reason:'room placement hook differs from 3.29.3b'});return;}
  Interceptor.attach(hook,{onEnter(args){
    try {
      const r=args[2], handle=r.readPointer(), data=handle.add(0x28).readPointer();
      const path=handle.add(8).readPointer().readUtf16String();
      const code=r.add(0x1b).readU8(), width=data.add(0x138).readU8()*23, height=data.add(0x139).readU8()*23;
      if(!path || !/\.arm$/i.test(path) || code>7 || !width || !height) return;
      send({type:'room',path,x:r.add(8).readS32()*23,y:r.add(0xc).readS32()*23,width,height,code});
    }catch(e){send({type:'error',message:'room placement: '+String(e)});}
  }});
}

// Area transitions and waypoints of the generated layout (Agents/docs/protocol/08-area-transitions.md §5).
// While generating, 0x141d7f700 walks each placed tile's .tmd records and calls 0x1420415e0(ctx, record):
//   ctx[1] → placed tile: +0x30 i16 height offset (steps of 7.8125), +0x36 u8 rotation/flip code 0..7
//   ctx[2] → i32 x, i32 y: the tile's grid origin;  ctx[3] → handle, *handle+0x28 → +0x60/+0x61 u8 size in tiles
//   record: i32 x, i32 y (tile-local cell), f32 z (+0x10), f32 angle (+0x14); +0x30 type, +0x38 .ao (handle+8 → path)
// The client spawns only its own doodads from them; server types (AreaTransition …) are skipped there, so their
// placement is computed here the way 0x14202f1c0 does it, with the constants read from the client.
function hookTileObjects(exe) {
  const at = va => exe.base.add(ptr(va).sub(IMAGE_BASE));
  const hook = at('0x1420415e0');
  const bytes = Array.from(new Uint8Array(hook.readByteArray(16))).map(b => b.toString(16).padStart(2, '0')).join('');
  if (bytes !== '488bc448895808555657415441554156') {
    send({ type: 'refused', reason: 'tile object hook: code does not match 3.29.3b' });
    return;
  }
  const codes = Array.from(new Uint8Array(at('0x142fa04b0').readByteArray(24)));
  const quarter = at('0x1436a3318').readFloat(), turn = at('0x1436a3674').readFloat();
  const noHeight = at('0x1436a48d4').readFloat();
  const path = h => { try { return h.isNull() ? null : h.add(8).readPointer().readUtf16String(); } catch (e) { return null; } };
  Interceptor.attach(hook, {
    onEnter(a) {
      try {
        const ctx = a[0], rec = a[1];
        const type = path(rec.add(0x30).readPointer());
        // Quest exits extend AreaTransition under their own name: The Twilight Strand's way into Lioneye's Watch is
        // Metadata/QuestObjects/SouthBeachTownEntrance (12-quests.md).
        // a1q4 placements are server objects too (12-quests.md). Keep them separate from navigable exits.
        const questObject = type && /^(Metadata\/Chests\/(QuestChests\/RhoaChest[23]?|TutorialSupportGemChest)|Metadata\/QuestObjects\/WaterCave\/GlyphWall)$/.test(type);
        if (!type || (!/AreaTransition|Waypoint$|TownEntrance$/.test(type) && !questObject)) return;
        const tile = ctx.add(8).readPointer();
        const code = Math.min(tile.add(0x36).readU8(), 8), heightOffset = tile.add(0x30).readS16();
        const origin = ctx.add(0x10).readPointer();
        const info = ctx.add(0x18).readPointer().readPointer().add(0x28).readPointer();
        const w = info.add(0x60).readU8(), h = info.add(0x61).readU8();
        const x = rec.readS32(), y = rec.add(4).readS32();
        const c = code < 8 ? codes.slice(code * 3, code * 3 + 3) : [0, 1, 1];
        const cells = [w * 23 - x - 1, x, h * 23 - y - 1, y];
        const gx = cells[c[1] + 2 * c[0]] + origin.readS32(), gy = cells[(c[0] === 0 ? 2 : 0) + c[2]] + origin.add(4).readS32();
        let rotation = rec.add(0x14).readFloat();
        if (code >= 4 && code < 8) rotation = turn - rotation;
        rotation += (code & 3) * quarter;
        while (rotation < 0) rotation += turn;
        while (rotation > turn) rotation -= turn;
        const z = rec.add(0x10).readFloat();
        send({ type: questObject ? 'quest-object' : 'object', objectType: type, model: path(rec.add(0x38).readPointer()), x: gx, y: gy, rotation,
               z: z === noHeight ? null : z - heightOffset * 7.8125, rawZ: z, heightOffset, code });
      } catch (e) {
        send({ type: 'error', message: 'tile object: ' + String(e) });
      }
    },
  });
}

// Every doodad the client places, rooms' doodads included (Agents/docs/protocol/14-monster-packs.md §3). 0x142042360
// (record, …) creates one client-side doodad from a placed record laid out like a .tmd record, already in world terms:
// i32 grid x, i32 grid y, f32 world x, f32 world y, f32 z (+0x10), f32 angle (+0x14), type handle +0x30, .ao handle +0x38.
// The Gateway matches them against data/room-points.json to find where rooms with monster points ("mapboss") were put.
// Sent in batches; walkmaps.py writes <area>_seed<seed>.doodads.json.
function hookDoodads(exe) {
  const hook = exe.base.add(ptr('0x142042360').sub(IMAGE_BASE));
  const bytes = Array.from(new Uint8Array(hook.readByteArray(16))).map(b => b.toString(16).padStart(2, '0')).join('');
  if (bytes !== '4c8bdc49895310535657415441554156') {
    send({ type: 'refused', reason: 'doodad hook: code does not match 3.29.3b (or another script hooks it)' });
    return;
  }
  const path = h => { try { return h.isNull() ? null : h.add(8).readPointer().readUtf16String(); } catch (e) { return null; } };
  let batch = [], timer = null;
  const flush = () => { timer = null; if (batch.length) { send({ type: 'doodads', doodads: batch }); batch = []; } };
  Interceptor.attach(hook, {
    onEnter(a) {
      try {
        const r = a[1];
        const model = path(r.add(0x38).readPointer());
        if (!model) return;
        batch.push([model, r.readS32(), r.add(4).readS32(), r.add(0x14).readFloat()]);
        if (batch.length >= 500) flush();
        else if (timer === null) timer = setTimeout(flush, 200);
      } catch (e) {
        send({ type: 'error', message: 'doodad: ' + String(e) });
      }
    },
  });
}
