// Verified addresses for the exact official 3.29.3b executable; no game-state writes.
const m = Process.getModuleByName('PathOfExile.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const streams = new Set(), reads = new Map(), writes = new Map();
let sequence = 0;
function frame(stream, direction) { return {stream:stream.toString(), direction, chunks:[], length:0, started:Date.now()}; }
function append(f, address, size) {
    if (size > 4194304 || f.length + size > 16777216) { f.error='capture size limit'; return; }
    try { f.chunks.push(new Uint8Array(address.readByteArray(size))); f.length += size; }
    catch(e) { f.error=String(e); }
}
function emit(f, accepted) {
    if (!f.length && !f.error) return;
    const bytes = new Uint8Array(f.length); let offset=0;
    for (const chunk of f.chunks) { bytes.set(chunk,offset); offset+=chunk.length; }
    send({kind:f.error?'capture-error':'bytes', sequence:++sequence, stream:f.stream,
        direction:f.direction, started:f.started, ended:Date.now(), accepted, length:f.length,
        opcode:f.length>=2 ? (bytes[0]<<8)|bytes[1] : null, error:f.error}, bytes.buffer);
}
Interceptor.attach(at('0x142148880'), {onEnter(a) {
    const stream=a[0].add(0xc288).readPointer();
    if (!stream.isNull()) streams.add(stream.toString());
}});
Interceptor.attach(at('0x14299e440'), {
    onEnter(a) {
        if (!streams.has(a[1].toString())) return;
        this.previous=reads.get(this.threadId); this.frame=frame(a[1],'S2C'); reads.set(this.threadId,this.frame);
    },
    onLeave(result) {
        if (!this.frame) return;
        if(this.previous) reads.set(this.threadId,this.previous); else reads.delete(this.threadId);
        // Failed reads retry; retain them explicitly instead of presenting retries as complete messages.
        emit(this.frame,result.toInt32()!==0);
    }
});
Interceptor.attach(at('0x140dfb630'), {
    onEnter(a) {
        const f=reads.get(this.threadId);
        if(f && a[0].toString()===f.stream) {this.frame=f;this.dest=a[1];this.count=a[2].toUInt32();}
    },
    onLeave() {if(this.frame) append(this.frame,this.dest,this.count);}
});
Interceptor.attach(at('0x14216e940'), {
    onEnter(a) {
        const stream=a[0].add(0xc288).readPointer(); streams.add(stream.toString());
        this.previous=writes.get(this.threadId);this.frame=frame(stream,'C2S');writes.set(this.threadId,this.frame);
    },
    onLeave() {
        if(this.previous) writes.set(this.threadId,this.previous); else writes.delete(this.threadId);
        emit(this.frame,true);
    }
});
Interceptor.attach(at('0x140dfb760'), {onEnter(a) {
    const f=writes.get(this.threadId); if(f && a[0].toString()===f.stream) append(f,a[1],a[2].toUInt32());
}});
send({kind:'ready',scope:'instance gameplay serialization and message-reader bytes',
    limitations:'Login traffic and side channels are excluded; accepted=false records are incomplete/retried reads. Validate both directions before playing.'});
