// Read-only prepared-client terrain observation. Attach before an area load.
// Names/offsets are pinned to 3.29.3b; this does not observe official server population code.
const client = Process.getModuleByName('Game.patched.exe');
function nativeAddress(p) { return '0x' + p.sub(client.base).add(ptr('0x140000000')).toString(16); }

Interceptor.attach(client.base.add(0x204d820), {
    onEnter(args) {
        this.generator = args[0];
        try {
            const world = this.generator.add(0x38).readPointer(), vtable = world.readPointer();
            send({ kind: 'world', vtable: nativeAddress(vtable),
                afterDoodads: nativeAddress(vtable.add(0x28).readPointer()) });
        } catch (error) { send({ error: error.message }); }
    },
    onLeave() {
        try {
            const params = this.generator.add(0x30).readPointer();
            const end = params.add(0x18).readPointer(), rooms = [];
            for (let graph = params.add(0x10).readPointer(); graph.compare(end) < 0; graph = graph.add(0x98)) {
                const roomEnd = graph.add(0x60).readPointer();
                for (let record = graph.add(0x58).readPointer(); record.compare(roomEnd) < 0; record = record.add(0x38)) {
                    const room = record.readPointer().add(0x28).readPointer();
                    const count = offset => room.add(offset + 8).readPointer().sub(room.add(offset).readPointer()).toInt32() / 0x30;
                    rooms.push({ wg: room.add(0xb).readU8(), hasState: !record.add(0x28).readPointer().isNull(),
                        monsterPoints: count(0x188), type3Points: count(0x1b8) });
                }
            }
            send({ kind: 'rooms', seed: params.readU32(), rooms });
        } catch (error) { send({ error: error.message }); }
    }
});
