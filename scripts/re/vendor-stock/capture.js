// 3.29.3c: message readers use the network stream, not a component blob reader.
const module = Process.getModuleByName('PathOfExile.exe');
const at = va => module.base.add(ptr(va).sub(ptr('0x140000000')));
const active = new Map();
Interceptor.attach(at('0x140df9ed0'), {
    onEnter(args) {
        this.frame = active.get(this.threadId);
        if (!this.frame || !args[0].equals(this.frame.stream)) return;
        this.dest = args[1];
        this.count = args[2].toUInt32();
    },
    onLeave() {
        if (!this.dest || !this.frame) return;
        try {
            if (this.count > 4194304 || this.frame.length + this.count > 4194304) throw new Error('size limit');
            const bytes = new Uint8Array(this.dest.readByteArray(this.count));
            this.frame.chunks.push(Array.from(bytes, b => b.toString(16).padStart(2, '0')).join(''));
            this.frame.length += this.count;
        } catch (e) { this.frame.error = String(e); }
    }
});
for (const [kind, address] of [['shop', '0x1421a88d0'], ['inventory', '0x140b17970']]) {
    Interceptor.attach(at(address), {
        onEnter(args) {
            this.frame = {kind, stream: args[1], chunks: [], length: 0};
            this.previous = active.get(this.threadId);
            active.set(this.threadId, this.frame);
        },
        onLeave() {
            if (this.previous) active.set(this.threadId, this.previous);
            else active.delete(this.threadId);
            const f = this.frame;
            // A short network read retries the whole message when more bytes arrive (01-login.md).
            // Its output buffer is incomplete; only publish the successful retry.
            if (f.stream.add(0x252).readU8() !== 0) return;
            send({kind: f.error ? 'capture-error' : f.kind, time: new Date().toISOString(),
                length: f.length, error: f.error, hex: f.chunks.join('')});
        }
    });
}
send({kind: 'ready', messages: ['0x8c', '0xc0'], method: 'stream read output',
    note: 'Inventory messages include player inventories; keep raw output private.'});
