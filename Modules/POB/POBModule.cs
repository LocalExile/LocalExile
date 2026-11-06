using GameServer.Modding;
using GameServer.Core.Protocol.Instance;
public sealed class POBModule : IModule
{
    public void Load(IModuleContext module)
    {
        PobRules.Stopping = module.Lifetime.Stopping;
        module.OnChatCommand("pob", "/pob <code> | cancel", async c =>
        {
            var conn = ConnOf(c.Player);
            if (c.Args.Count == 1 && c.Args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase))
            { conn.BuildImport.Clear(); await conn.Commands!.Reply("Импорт PoB отменён."); }
            else await PobRules.Prepare(conn, c.Args.ToArray());
        });
        module.OnPacket(InstanceChat.ChatId, async p =>
        {
            var conn = ConnOf(p.Player);
            string text = InstanceChat.ReadChat(p.Bytes.Span).Text;
            if (text.StartsWith("/pob ", StringComparison.OrdinalIgnoreCase))
            {
                string[] args = text[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length == 1 && args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase))
                { conn.BuildImport.Clear(); await conn.Commands!.Reply("Импорт PoB отменён."); }
                else await PobRules.Prepare(conn, args);
                return true;
            }
            return await PobRules.Chat(conn, text);
        });
    }
}
