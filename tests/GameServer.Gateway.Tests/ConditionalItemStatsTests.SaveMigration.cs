using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Legacy_json_saves_migrate_without_losing_item_fields()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-504-json-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (bool futureItemField in new[] { false, true })
            {
                using var db = new SaveDatabase(Path.Combine(folder, futureItemField ? "future.db" : "legacy.db"));
                string extra = futureItemField ? ",\"FutureItemFlag\":7" : "";
                string json = "{\"Inventories\":{\"1\":[{\"Id\":19,\"X\":2,\"Y\":3,\"Item\":{\"Type\":123,\"Sockets\":[{\"Colour\":0,\"Gem\":{\"Type\":124}}]" + extra
                    + "}},{\"Id\":20,\"X\":3,\"Y\":4,\"Item\":{\"Type\":125,\"Foreseeing\":true,\"ForeseeSeed\":987}}]},\"FutureCharacterField\":{\"value\":8},"
                    + "\"HideoutPlacements\":{\"h\":{\"p\":{\"X\":2,\"Y\":3,\"Rotation\":0.0}}}}";
                using (var insert = db.Command("INSERT INTO characters (name,save) VALUES ('legacy',$json)", ("$json", json)))
                    insert.ExecuteNonQuery();
                using (var insert = db.Command("INSERT INTO deleted_characters (name,deleted_utc,save) VALUES ('deleted','2026-11-01',$json)", ("$json", json)))
                    insert.ExecuteNonQuery();
                if (futureItemField)
                {
                    Assert.Throws<InvalidDataException>(() => SaveRows.Migrate(db, _ => { }));
                    Assert.False(db.Relational);
                    using var count = db.Command("SELECT count(*) FROM character_saves");
                    Assert.Equal(0L, count.ExecuteScalar());
                }
                else
                {
                    Assert.Equal(2, SaveRows.Migrate(db, _ => { }));
                    Assert.True(db.Relational);
                    var back = SaveRows.LoadCharacter(db, "legacy")!;
                    var items = back.Inventories[1];
                    Assert.Equal((uint)19, items[0].Id);
                    Assert.False(items[0].Item.Foreseeing);
                    Assert.Equal((uint)0, items[0].Item.ForeseeSeed);
                    Assert.Equal((uint)124, items[0].Item.Sockets![0].Gem!.Type);
                    Assert.True(items[1].Item.Foreseeing);
                    Assert.Equal((uint)987, items[1].Item.ForeseeSeed);
                    Assert.Equal(8, back.Unknown!["FutureCharacterField"].GetProperty("value").GetInt32());
                    Assert.Equal(new HideoutPlacement(2, 3, 0), back.HideoutPlacements!["h"]["p"]);
                    Assert.Equal(0, SaveRows.Migrate(db, _ => { }));
                }
                using var original = db.Command("SELECT save FROM characters WHERE name='legacy'");
                Assert.Equal(json, original.ExecuteScalar());
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
