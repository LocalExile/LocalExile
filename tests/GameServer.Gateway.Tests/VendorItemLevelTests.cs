using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public class VendorItemLevelTests
{
    [Fact]
    public void Vendor_membership_prices_and_generated_requirements_follow_the_reviewed_rules()
    {
        Saves.Open(Path.Combine(Path.GetTempPath(),"LocalExile-gem-layout-"+Guid.NewGuid().ToString("N")+".db"));
        var catalog = new ItemCatalog("data");
        var rewards = QuestRewardCatalog.Load();
        var npcs = NpcCatalog.Load(); var skills = new SkillCatalog();
        var kiracHideout = npcs.Find("Metadata/NPC/Epilogue/KiracHideout")!;
        var kiracSource = npcs.TalkSource(kiracHideout);
        Assert.NotEqual(kiracHideout.Row,kiracSource.Row);
        var kiracTalks = npcs.TalksOf(kiracHideout).ToArray();
        Assert.Equal(npcs.TalksOf(kiracSource),kiracTalks);
        Assert.Contains(kiracTalks,t => t.Category == "BuyItems");
        Assert.All(kiracTalks,t => Assert.Equal(t,npcs.TalkOf(kiracHideout,t.Index)));
        var nessa = npcs.Find("Metadata/NPC/Act1/Nessa")!;
        var alias = nessa with { Row = -1 };
        var rewardTalk = npcs.TalksOf(nessa).First(t => t.RewardOffer is not null && t.RequiredFlags.Count > 0);
        Assert.True(npcs.HasAvailableReward(alias,rewardTalk.RewardOffer!.Value,rewardTalk.RequiredFlags.ToHashSet()));
        Assert.Equal(npcs.HasAvailableReward(nessa,rewardTalk.RewardOffer.Value,new HashSet<string>()),
            npcs.HasAvailableReward(alias,rewardTalk.RewardOffer.Value,new HashSet<string>()));
        var gems = GemVendorStock.For(catalog,npcs,skills);
        Assert.Same(gems,GemVendorStock.For(catalog,npcs,skills));
        Assert.NotSame(gems,GemVendorStock.For(catalog,npcs,skills,supportGems:false));
        Assert.NotSame(gems,GemVendorStock.For(catalog,npcs,skills,hardMode:true));
        var flags = rewards.Offers.Where(o => o.TakenFlag is not null).Select(o => o.TakenFlag!).ToHashSet();
        flags.Add(GemVendorStock.LillyUnlock); flags.Add(GemVendorStock.SiosaUnlock);
        foreach (var (npc,normal,count) in new[] { ("Act1/Nessa",15,156), ("Act2/Yeena",25,71),
            ("Act3/ClarissaTown",35,90), ("Act4/PetarusVanja",42,61) })
        {
            Assert.Equal(normal,NpcShops.CampaignItemLevel("Metadata/NPC/"+npc,ItemRarity.Normal));
            Assert.Equal(normal-1,NpcShops.CampaignItemLevel("Metadata/NPC/"+npc,ItemRarity.Magic));
            Assert.Equal(normal-2,NpcShops.CampaignItemLevel("Metadata/NPC/"+npc,ItemRarity.Rare));
            Assert.Equal(count,gems.Stock("Metadata/NPC/"+npc,2,flags).Count);
            Assert.Empty(gems.Stock("Metadata/NPC/"+npc,2,new HashSet<string>()));
        }
        Assert.Empty(gems.Stock("Metadata/NPC/Act3/Siosa",2,new HashSet<string>()));
        Assert.NotEmpty(gems.Stock("Metadata/NPC/Act3/Siosa",2,new HashSet<string>{GemVendorStock.SiosaUnlock}));
        Assert.Empty(gems.Stock("Metadata/NPC/Epilogue/Lilly",2,new HashSet<string>()));
        Assert.Equal(459,gems.Stock("Metadata/NPC/Epilogue/Lilly",2,flags).Count);
        const string hideoutLilly = "Metadata/NPC/Epilogue/LillyHideout";
        var newWitch = gems.Stock(hideoutLilly,1,new HashSet<string>(),level:2);
        Assert.Equal(459,newWitch.Count);
        Assert.Empty(gems.Stock("Metadata/NPC/Act6/Lilly",1,new HashSet<string>(),level:2));
        Assert.Contains(newWitch,row => skills.PlayerLevelReq(catalog.Gem(catalog.Row(row)!)!.Variant(0).Effect,1) > 2);
        Assert.Empty(gems.Stock(hideoutLilly,1,flags,level:2,rules:LeagueRules.Standard with { GemVendors = [] }));
        Assert.All(gems.Stock(hideoutLilly,1,flags,level:2,rules:LeagueRules.Standard with { SellsSupportGems = false }),
            row => Assert.False(catalog.Gem(catalog.Row(row)!)!.IsSupport));
        // A cached catalogue must not retain another character's unlocks or league restrictions.
        Assert.Equal(newWitch,gems.Stock(hideoutLilly,1,new HashSet<string>(),level:2));
        Assert.False(LeagueRules.Standard.FullGemStock);
        var prices = VendorPrices.For(catalog);
        Assert.Same(prices,VendorPrices.For(catalog));
        Assert.NotSame(prices,VendorPrices.For(catalog,true));
        Assert.NotSame(prices,VendorPrices.For(new ItemCatalog("data")));
        var portal = prices.Price(ItemCatalog.Create(catalog.Find("Portal Scroll")!))!;
        Assert.Equal((catalog.Find("Scroll of Wisdom")!.Hash,3u),Assert.Single(portal.Price));
        var map = prices.Price(ItemCatalog.Create(catalog.Find("Metadata/Items/Maps/MapKeyTier15")!)
            with { ItemLevel=82,Rarity=ItemRarity.Rare,Identified=false })!;
        Assert.Contains((catalog.Find("Gold")!.Hash,8000u),map.Price);
        Assert.Contains((catalog.Find("Orb of Chance")!.Hash,4u),map.Price);
        Assert.Equal(82,NpcShops.CampaignItemLevel("Metadata/NPC/Epilogue/KiracHideout",ItemRarity.Rare,82));
        var inventory = InstanceInventories.AddInventory(1024,NpcShops.NativeTab,NpcShops.Manager,[],0x60);
        Assert.Equal(new byte[] {71,117,12,11,0x60},inventory[7..12]);
        // Independent digests of (tab,x,y,base Id) from the original client's five captured gem assortments.
        foreach (var (npc,expected) in new[] {
            ("Act1/Nessa","c1f3b93c1313e2b38dba4b3928d5cbac1a9dba62acd9864b894e74c9363c6442"),
            ("Act2/Yeena","acb98183dcdad91c644f28ea461b135a3ade2df974d728cd474bc76a7bf9799b"),
            ("Act3/ClarissaTown","e2bfd31bc0169d989ef3ef5e2c1527da0608d22942e43acdbf9585d6e2c18e97"),
            ("Act4/PetarusVanja","62da2d5f43a9aee56047533884b3d076b9f2dab5a45be867fbadc19ba1547f8b"),
            ("Epilogue/LillyHideout","e7d6240011ab0448e68451c25247b5612dec31235fbebbfcdca71942fbd70e13") })
        {
            string id = "Metadata/NPC/"+npc;
            uint next = 0;
            var notes = new List<string>();
            var window = VendorStock.Open("Layout"+Guid.NewGuid().ToString("N"),"Standard",id,91,catalog,
                new SkillCatalog(),gems.Stock(id,2,flags),() => ++next,LeagueRules.Standard,false,notes,random:new Random(2));
            Assert.Empty(notes);
            var tabs = window.Tabs.Where(t => t.Items.Any(i => catalog.Gem(catalog.ByHash(i.Item.TypeHash)!) is not null)).ToArray();
            string positions = string.Join('\n',tabs.SelectMany((t,index) => t.Items.Select(i =>
                $"{index}:{i.X}:{i.Y}:{catalog.ByHash(i.Item.TypeHash)!.Id}")));
            Assert.Equal(expected,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(positions))).ToLowerInvariant());
        }
    }

    [Fact]
    public void Stock_survives_reopening_purchases_and_reload_and_rotates_partially_with_atomic_multicurrency_payment()
    {
        Saves.Open(Path.Combine(Path.GetTempPath(),"LocalExile-vendors-"+Guid.NewGuid().ToString("N")+".db"));
        var catalog = new ItemCatalog("data"); var skills = new SkillCatalog();
        string name = "Vendor"+Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(name,catalog); var stash = AccountStash.For(name,catalog);
        var notes = new List<string>();
        VendorStock.Window Open(int level) => VendorStock.Open(name,"Standard","Metadata/NPC/Act1/Tarkleigh",level,
            catalog,skills,[],items.NewItemId,LeagueRules.Standard,false,notes,random:new Random(7401));
        var first = Open(91); Assert.Empty(notes); Assert.Equal(60,first.Finite.Count);
        string saved = CharacterSaves.ModuleData(name,first.Key)!;
        var again = Open(91); Assert.Equal(saved,CharacterSaves.ModuleData(name,first.Key));
        Assert.Equal(first.Finite.Values.Order(),again.Finite.Values.Order());
        var generated = first.Tabs.SelectMany(t => t.Items).Where(i => first.Finite.ContainsKey(i.Id)).ToArray();
        Assert.Contains(generated,i => i.Item.Rarity==ItemRarity.Magic);
        Assert.True(generated.Select(i => i.Item.Sockets.Count).Distinct().Count()>1);
        Assert.All(generated,i => { Assert.Equal(15-(int)i.Item.Rarity,i.Item.ItemLevel); Assert.NotEmpty(i.Item.Price); });
        var token = first.Finite.First().Value;
        var wisdom = catalog.Find("Scroll of Wisdom")!; var chance = catalog.Find("Orb of Chance")!;
        var gold = catalog.Find(ItemDrops.GoldId)!;
        items.Add(0,0,0,ItemCatalog.Create(wisdom) with { StackSize=5 });
        stash.AddGold(100);
        var offer = ItemCatalog.Create(catalog.Find("Iron Ring")!) with {
            Price=[(wisdom.Hash,2),(chance.Hash,1),(gold.Hash,50)] };
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width,t.Height,t.Class,t.MaxStack) : null;
        bool Consume() => VendorStock.Consume(name,first.Key,token);
        Assert.Null(ItemMoves.PurchaseVendor(items,stash,stash,offer,catalog,Shape,Consume));
        Assert.Equal(5,items.Count(0,wisdom.Hash)); Assert.Equal(100u,stash.Gold);
        Assert.Equal(saved,CharacterSaves.ModuleData(name,first.Key));
        items.Add(0,1,0,ItemCatalog.Create(chance));
        Assert.Null(ItemMoves.PurchaseVendor(items,stash,stash,offer,catalog,Shape,Consume,0,0,0));
        Saves.FailWrite = key => key == name;
        try { Assert.Throws<IOException>(() => ItemMoves.PurchaseVendor(items,stash,stash,offer,catalog,Shape,Consume)); }
        finally { Saves.FailWrite = null; }
        PlayerInventories.Forget(name); AccountStash.Forget(name);
        items=PlayerInventories.For(name,catalog); stash=AccountStash.For(name,catalog);
        Assert.Equal(saved,CharacterSaves.ModuleData(name,first.Key));
        Assert.NotNull(ItemMoves.PurchaseVendor(items,stash,stash,offer,catalog,Shape,Consume));
        Assert.Equal(3,items.Count(0,wisdom.Hash)); Assert.Equal(0,items.Count(0,chance.Hash)); Assert.Equal(50u,stash.Gold);
        Assert.Null(ItemMoves.PurchaseVendor(items,stash,stash,offer,catalog,Shape,Consume));
        Assert.DoesNotContain(token,Open(91).Finite.Values);
        PlayerInventories.Forget(name); items=PlayerInventories.For(name,catalog);
        Assert.DoesNotContain(token,Open(91).Finite.Values);
        var rotated=Open(92);
        Assert.InRange(first.Finite.Values.Intersect(rotated.Finite.Values).Count(),39,40);
        var allNew=Open(94); Assert.Empty(first.Finite.Values.Intersect(allNew.Finite.Values));
        VendorStock.Window Kirac(string character) => VendorStock.Open(character,"Standard","Metadata/NPC/Epilogue/KiracHideout",91,
            catalog,skills,[],items.NewItemId,LeagueRules.Standard,false,notes,random:new Random(2),account:stash);
        var kirac=Kirac(name);
        Assert.Equal(18,kirac.Finite.Count);
        var kiracItems = Assert.Single(kirac.Tabs).Items;
        Assert.Equal("Orb of Unmaking",catalog.ByHash(kiracItems[0].Item.TypeHash)!.Name);
        Assert.Equal("Kalguuran Scarab",catalog.ByHash(kiracItems[1].Item.TypeHash)!.Name);
        for (int i=0;i<kiracItems.Count;i++)
        {
            Assert.Equal(i/11,kiracItems[i].X);
            Assert.Equal(i%11,kiracItems[i].Y);
        }
        Assert.All(kirac.Tabs.SelectMany(t=>t.Items).Where(i=>catalog.ByHash(i.Item.TypeHash)!.Class=="MapKey"),
            i=>Assert.Equal("Metadata/Items/Maps/MapKeyTier1",catalog.ByHash(i.Item.TypeHash)!.Id));
        Assert.Equal(1000,KiracProgress.For(stash).ShopRerollCost);
        Assert.Equal(kirac.Finite.Values.Order(),Kirac("AnotherCharacter").Finite.Values.Order());
        Assert.True(KiracProgress.Complete(stash,"MapWorldsBeach",1));
        Assert.False(KiracProgress.Complete(stash,"MapWorldsBeach",5));
        Assert.Equal("5",stash.ModuleData("vendors.highestTier"));
        Assert.Equal(2,KiracProgress.For(stash).Tier);
        Assert.Equal(1500,KiracProgress.For(stash).ShopRerollCost);
        Assert.Empty(kirac.Finite.Values.Intersect(Kirac(name).Finite.Values));
        Assert.True(KiracProgress.Complete(stash,"MapWorldsStrand",2));
        Assert.Equal(2,KiracProgress.For(stash).Tier);
        Assert.True(KiracProgress.Complete(stash,"MapWorldsDunes",2));
        Assert.Equal(3,KiracProgress.For(stash).Tier);
        AccountStash.Forget(name); stash=AccountStash.For(name,catalog);
        Assert.Equal(3,KiracProgress.Completed(stash).Count);
        Assert.Equal("5",stash.ModuleData("vendors.highestTier"));
    }
}
