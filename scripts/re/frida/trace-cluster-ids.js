// Logs the cluster-jewel node constructor (FUN_141f114f0): the size tag, the template value, the cell's start and
// total, and the node id it produces. Also logs the id function (FUN_141f0e770) with the node's slot row and position.
//   python scripts/re/frida/run.py Game.patched.exe scripts/re/frida/trace-cluster-ids.js --seconds 120 --log ...
const module = Process.getModuleByName('Game.patched.exe');
const base = module.base;
const at = va => base.add(ptr(va).sub(ptr('0x140000000')));

Interceptor.attach(at(0x141f114f0), {
    onEnter(args) {
        try {
            const ctx = args[0];
            const start = ctx.add(8).readPointer().readS32();
            const total = ctx.add(16).readPointer().readS32();
            send({ ctor: 1, size: args[1].toInt32(), value: args[3].toInt32(), start, total });
        } catch (e) {
            send({ ctorError: e.message });
        }
    },
    onLeave(retval) {
        send({ ctorId: retval.toInt32() });
    }
});

Interceptor.attach(at(0x141f0e770), {
    onEnter(args) {
        this.node = args[0];
    },
    onLeave(retval) {
        try {
            const node = this.node;
            send({
                id: retval.toInt32(),
                slot: node.add(0x90).readS32(),
                size: node.add(0x1c).readS32(),
                position: node.add(0x20).readS32()
            });
        } catch (e) {
            send({ idError: e.message });
        }
    }
});
