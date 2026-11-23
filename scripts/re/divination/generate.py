"""Generate card rewards from client descriptions and reviewed pool rules."""
import json
import re
import sqlite3
from .sources import SOURCE_RESTRICTED, LEAGUE_SPECIFIC, RING_IMPLICITS


def exact_reward(kind, label, db, world):
    if kind == 'unique':
        # Maps and the supplemental relic catalog use explicit members, including exact named rewards.
        return world.execute("SELECT 1 FROM unique_items WHERE enabled=1 AND name=? AND base_type NOT LIKE 'Metadata/Items/Maps/%' AND base_type NOT LIKE 'Metadata/Items/Relics/%'", (label,)).fetchone() is not None
    if kind == 'gem':
        return db.execute('SELECT 1 FROM SkillGems g JOIN BaseItemTypes b ON b._row=g.BaseItemTypesKey WHERE b.Name=?', (label,)).fetchone() is not None
    return label != 'Map' and db.execute('SELECT 1 FROM BaseItemTypes WHERE Name=? OR Id=?', (label,label)).fetchone() is not None


def named_rewards(db, world):
    rows=db.execute("SELECT b.Id,b.Name,c.StackSize,c.Description,c.DescriptionHardmode FROM BaseItemTypes b JOIN CurrencyItems c ON c.BaseItemTypesKey=b._row JOIN ItemClasses i ON i._row=b.ItemClassesKey WHERE i.Id='DivinationCard' ORDER BY b.Id").fetchall()
    sql=['CREATE TABLE divination_card_rewards (',
     '    card_id TEXT PRIMARY KEY, card_name TEXT NOT NULL, stack_size INTEGER NOT NULL CHECK (stack_size > 0),',
     '    reward_kind TEXT NOT NULL, reward_name TEXT NOT NULL, reward_count INTEGER NOT NULL CHECK (reward_count > 0),',
     '    corrupted INTEGER NOT NULL, two_implicit INTEGER NOT NULL, quality INTEGER NOT NULL, quality_max INTEGER NOT NULL, gem_level INTEGER NOT NULL,',
     '    six_link INTEGER NOT NULL, item_level INTEGER NOT NULL, white_sockets INTEGER NOT NULL, enabled INTEGER NOT NULL,',
     '    source TEXT NOT NULL, notes TEXT NOT NULL',');']
    def quote(v):return str(v) if isinstance(v,int) else "'"+str(v).replace("'","''")+"'"
    enabled=0
    for card,name,stack,text,hardmode in rows:
        header=re.match(r'<(\w+)>\{([^}]+)\}',text)
        text=re.sub(r'<size:\d+>\{','',text)
        header=re.match(r'<(\w+)>\{([^}]+)\}',text)
        kind={'uniqueitem':'unique','currencyitem':'currency','whiteitem':'normal','normal':'normal','rareitem':'rare',
              'magicitem':'magic','gemitem':'gem','divination':'card'}.get(header[1] if header else '', 'unsupported')
        label=header[2] if header else ''
        count=1;gem_level=0;quality=0;quality_max=0;item_level=0;map_tier=0;six_link=False;corrupted=False;two_implicit=False;white=False
        if m:=re.match(r'([\d,]+)x (.+)',label):count=int(m[1].replace(',',''));label=m[2]
        if label.startswith('Six-Link '):six_link=True;label=label.removeprefix('Six-Link ')
        if kind=='gem' and (m:=re.match(r'Level (\d+) (.+)',label)):gem_level=int(m[1]);label=m[2]
        unsupported=kind=='unsupported' or bool(hardmode and hardmode!=text)
        plain=re.sub(r'<[^>]+>|[{}]','',text)
        for line in [s.strip() for s in plain.splitlines()[1:] if s.strip()]:
            if m:=re.fullmatch(r'Item Level:\s*(\d+)',line):item_level=int(m[1])
            elif m:=re.fullmatch(r'Map Tier:\s*(\d+)',line):map_tier=int(m[1])
            elif m:=re.fullmatch(r'Quality:\s*\+(\d+)%',line):quality=int(m[1])
            elif m:=re.fullmatch(r'Quality:\s*\+(\d+)-(\d+)%',line):quality=int(m[1]);quality_max=int(m[2])
            elif line=='Corrupted':corrupted=True
            elif line=='Two-Implicit':two_implicit=True
            elif line in ['Six-Linked','Six-Link']:six_link=True
            elif line in ['6 White Sockets','Six White Sockets']:white=True
            else:unsupported=True
        alias=kind=='gem' and label in ('Empower','Enhance','Enlighten')
        gem_pool=kind=='gem' and label in {'Trap Gem','Exceptional Support Gem','Support Gem','Transfigured Gem',
            'Empower, Enhance or Enlighten','Gem','Transfigured Golem Gem','Chaos Gem','Spell Gem',
            'Awakened Exceptional Gem','Golem Gem','Minion Gem','Aura Gem','Vaal Gem'}
        named_gem_range=kind=='gem' and label=='Portal' and quality_max>0
        map_alias=False
        if label=='Map' and map_tier and kind in ('normal','rare'):
            base=db.execute('SELECT b.Id FROM MapKeys m JOIN BaseItemTypes b ON b._row=m.BaseItemType WHERE m.MapTier=?',(map_tier,)).fetchall()
            if len(base)==1:label=base[0][0];map_alias=True
        if map_tier and not map_alias:unsupported=True
        unsupported |= not (alias or map_alias or gem_pool or named_gem_range) and not exact_reward(kind, label, db, world)
        if alias:label+=' Support'
        if gem_pool:kind='gem_pool'
        disabled=plain.strip()=='Disabled'
        if disabled:kind='disabled';unsupported=True
        enabled+=not unsupported
        vals=[card,name,stack,kind,label,count,int(corrupted),int(two_implicit),quality,quality_max,gem_level,int(six_link),item_level,
              int(white),int(not unsupported),'3.29.3d CurrencyItems.Description; client-tagged gem pool' if gem_pool else '3.29.3d CurrencyItems.Description; exact named rewards only',
              'Client 3.29.3d reward description is Disabled; preserve this game restriction.' if disabled else
              'Unresolved pool, extra constraint or description; refuse without consuming cards.' if unsupported else
              'Advertised category from client GemTags and creatable variants; local uniform selection, not recovered official card-specific weights.' if gem_pool else
              'Exact named reward; factory also refuses missing types or modifiers.']
        sql.append('INSERT INTO divination_card_rewards VALUES ('+', '.join(map(quote,vals))+');')
    return "\n".join(sql)+"\n"


def equipment_rewards(db, world, base_sql):
    seed=sqlite3.connect(':memory:');seed.row_factory=sqlite3.Row
    seed.executescript(base_sql)
    sql=['-- Additional typed reward constraints and explicit current-client pool members. Eligible members are sampled',
         '-- uniformly as a local server policy unless a member weight is supplied; official exchange weights are unknown.',
    '''CREATE TABLE divination_reward_rules (
        card_id TEXT PRIMARY KEY REFERENCES divination_card_rewards(card_id),
        influences INTEGER NOT NULL, random_influences INTEGER NOT NULL CHECK (random_influences BETWEEN 0 AND 2),
        affix TEXT NOT NULL, fractured INTEGER NOT NULL, links INTEGER NOT NULL CHECK (links BETWEEN 0 AND 6),
        implicits INTEGER NOT NULL, unidentified INTEGER NOT NULL, modifiers INTEGER NOT NULL, special TEXT NOT NULL
    ) STRICT;''',
    '''CREATE TABLE divination_reward_members (
        card_id TEXT NOT NULL REFERENCES divination_card_rewards(card_id), member_kind TEXT NOT NULL,
        member_id TEXT NOT NULL, base_type TEXT NOT NULL, weight INTEGER NOT NULL CHECK (weight > 0),
        PRIMARY KEY (card_id, member_kind, member_id, base_type)
    ) STRICT;''']
    sql.append('CREATE TABLE divination_reward_implicit_mods (card_id TEXT NOT NULL REFERENCES divination_card_rewards(card_id), mod_id TEXT NOT NULL, PRIMARY KEY (card_id, mod_id)) STRICT;')
    def q(v):return str(v) if isinstance(v,int) else "'"+str(v).replace("'","''")+"'"
    unique_weights=dict(world.execute('SELECT name,weight FROM unique_weights'))
    def general(u):return not u['encounter_only'] and not u['name'].startswith('Replica ') and u['name'] not in SOURCE_RESTRICTED
    league_names=LEAGUE_SPECIFIC
    classes=dict(db.execute('SELECT _row,Id FROM ItemClasses'));tags=dict(db.execute('SELECT _row,Id FROM Tags'))
    bases=[dict(r,Class=classes[r['ItemClassesKey']],Tags={tags[x] for x in json.loads(r['TagsKeys'])}) for r in db.execute('SELECT * FROM BaseItemTypes')]
    byid={r['Id']:r for r in bases};byrow={r['_row']:r for r in bases}
    weapons={'One Hand Sword','Thrusting One Hand Sword','One Hand Axe','One Hand Mace','Sceptre','Dagger','Rune Dagger','Claw','Wand','Two Hand Sword','Two Hand Axe','Two Hand Mace','Staff','Warstaff','Bow'}
    armour={'Body Armour','Helmet','Gloves','Boots','Shield'};jewellery={'Ring','Amulet','Belt'}
    notdropped=['Royale','Demigod','Talisman','Breach','Synthesis','Fishing','Test','Unarmed','Atlas','Expedition','Heist','Relic','Basilisk','Crusader','Eyrie','Elder','Shaper','Necropolis','Sanctum','Ultimatum','Legion']
    generic_labels=set(classes.values())|{'Item','Weapon','One-Hand Weapon','Two-Hand Weapon','Armour','Body Armour','Jewellery','Jewel','Cluster Jewel','Flask','Staff','Axe','Sword','Dagger'}
    plain_jewels={'Crimson Jewel','Cobalt Jewel','Viridian Jewel','Murderous Eye Jewel','Searching Eye Jewel','Hypnotic Eye Jewel','Ghastly Eye Jewel'}
    def dropped(t):return t['DropLevel']>=1 and t['Name'] and ('not_for_sale' not in t['Tags'] or 'atlas_base_type' in t['Tags']) and not any(x in t['Id'] and not(x=='Atlas' and 'atlas_base_type' in t['Tags']) for x in notdropped)
    def category(label,unique=False):
        norm=label.replace('Six-Linked ','').replace('Six-Link ','').replace('Five-Linked ','').replace('Five-Link ','')
        exact=[b for b in bases if b['Name']==norm] if norm not in generic_labels else []
        if exact:return exact if unique else [b for b in exact if b['DropLevel']>0 and 'Royale' not in b['Id']]
        if norm.startswith('Experimented'):
            return [b for b in bases if 'experimental_base' in b['Tags'] and (norm=='Experimented Item' or b['Class'] in jewellery)]
        if norm=='Talisman':return [b for b in bases if re.fullmatch(r'Metadata/Items/Amulets/Talismans/Talisman[123]_\d+',b['Id'])]
        if norm=='Jewel' and not unique:return [b for b in bases if b['Name'] in plain_jewels and 'Royale' not in b['Id']]
        accept={'Item':weapons|armour|jewellery|{'Quiver','Jewel','AbyssJewel'},'Weapon':weapons,
            'One-Hand Weapon':weapons-{'Two Hand Sword','Two Hand Axe','Two Hand Mace','Staff','Warstaff','Bow'},
            'Two-Hand Weapon':{'Two Hand Sword','Two Hand Axe','Two Hand Mace','Staff','Warstaff','Bow'},
            'Armour':armour,'Body Armour':{'Body Armour'},'Jewellery':jewellery,'Jewel':{'Jewel','AbyssJewel'},
            'Cluster Jewel':{'Jewel'},'Flask':{'LifeFlask','ManaFlask','HybridFlask','UtilityFlask','CriticalUtilityFlask'},
            'Staff':{'Staff','Warstaff'},'Axe':{'One Hand Axe','Two Hand Axe'},'Sword':{'One Hand Sword','Thrusting One Hand Sword','Two Hand Sword'},
            'Dagger':{'Dagger','Rune Dagger'}}.get(norm,{norm})
        if norm=='Item' and unique:accept|={'LifeFlask','ManaFlask','HybridFlask','UtilityFlask','CriticalUtilityFlask'}
        return [b for b in bases if b['Class'] in accept and (unique or dropped(b) or b['Name'] in plain_jewels and 'Royale' not in b['Id'] or norm=='Cluster Jewel' and 'PassiveTreeExpansion' in b['Id']) and (norm!='Cluster Jewel' or 'PassiveTreeExpansion' in b['Id'])]
    scarabs={byrow[x]['Id'] for r in db.execute('SELECT Items FROM Scarabs') for x in json.loads(r[0])}
    essences={r[0] for r in db.execute('SELECT b.Id FROM Essences e JOIN BaseItemTypes b ON b._row=e.BaseItemTypesKey')}
    fossils={r[0] for r in db.execute("SELECT b.Id FROM DelveCraftingModifiers e JOIN BaseItemTypes b ON b._row=e.BaseItemTypesKey WHERE b.Id LIKE 'Metadata/Items/Currency/CurrencyDelveCrafting%' AND b.Name!=''")}
    unique_rows=[dict(r) for r in world.execute('SELECT * FROM unique_items WHERE enabled=1 ORDER BY name,base_type')]
    maps=[r[0] for r in db.execute('SELECT DISTINCT Name FROM UniqueMaps ORDER BY Name')]
    affixes=["of Celebration","of Redemption","of the Conquest","of the Cheetah","of Crafting","of Farrul","of Potency","of Bameth","Electrocuting","Unassailable","Overpowering","Bloodthirsty","Merciless","Perandus'","Seraphim's","Dictator's","Surgeon's","Elreon's","Malicious","Glyphic","Flaring","Fecund","Vivid","Runic","Punishing"]
    named_affix={"Elreon's":'id:ReduceGlobalFlatManaCostStrIntMasterVendor','of Farrul':'id:GrantsCatAspectCrafted',
        'of the Conquest':'id:CullingStrikeInfluence1','of Crafting':'id:StrIntMasterItemGenerationCanHaveMultipleCraftedMods'}
    def base_members(ts):return [('base',t['Id'],'',1) for t in ts]
    def named(names):
        return [('unique',u['name'],u['base_type'],1) for u in unique_rows if u['name'] in names]+[('map',n,'',1) for n in names if n in maps]
    enabled=[];remaining=[]
    for reward in seed.execute("SELECT * FROM divination_card_rewards WHERE enabled=0 AND reward_kind!='disabled' ORDER BY card_id"):
        r=dict(reward);raw=db.execute('SELECT Description FROM CurrencyItems c JOIN BaseItemTypes b ON b._row=c.BaseItemTypesKey WHERE b.Id=?',(r['card_id'],)).fetchone()[0]
        plain=re.sub(r'<[^>]+>|[{}]','',raw);lines=[s.strip() for s in plain.splitlines() if s.strip()]
        label=re.sub(r'^[\d,]+x ','',lines[0]) if lines else '';label=re.sub(r'^(?:Six|Five)-Link(?:ed)? ','',label).removesuffix(' (Culling Strike)')
        rule=dict(influences=0,random_influences=0,affix='',fractured=0,links=6 if 'Six-Link' in plain else 5 if 'Five-Link' in plain else 0,implicits=0,unidentified=0,modifiers=0,special='')
        unknown=[]
        for line in lines[1:]:
            if line in ['Shaper Item','Elder Item','Crusader Item','Redeemer Item','Hunter Item','Warlord Item']:
                rule['influences']|=dict(Shaper=1,Elder=2,Crusader=4,Redeemer=8,Hunter=16,Warlord=32)[line.split()[0]]
            elif line in ['Shaper + Elder Item','Shaper + Hunter Item']:rule['influences']=3 if 'Elder' in line else 17
            elif line=='Double-Influenced Item':rule['random_influences']=2
            elif line=='Influenced Item':rule['random_influences']=1
            elif line=='Fractured':rule['fractured']=1
            elif line=='Unidentified Corrupted':rule['unidentified']=1;r['corrupted']=1
            elif line=='Modifiers: 8':rule['modifiers']=8
            elif line=='Two-Implicit':rule['implicits']=2
            elif line=='1-2 Implicit':rule['implicits']=-2
            elif re.fullmatch(r'(Item Level|Map Tier):\s*\d+',line) or line in ['Corrupted','Six-Link','Six-Linked']:pass
            else:unknown.append(line)
        members=[];supported=False
        if 'Primordial' in unknown:unknown.remove('Primordial');rule['special']='primordial'
        if unknown==['Vestigial']:unknown=[];rule['special']='vestigial'
        if unknown==['Foulborn']:unknown=[];rule['special']='foulborn'
        if unknown==['Elevated Item']:unknown=[];rule['special']='elevated';rule['affix']='elevated'
        if unknown==['Double-Veiled Item']:unknown=[];rule['special']='double_veiled'
        if unknown==['Perfect Eldritch Implicit Modifier']:unknown=[];rule['special']='perfect_eldritch'
        if unknown==['Delirium: 100%']:unknown=[];rule['special']='delirium'
        if unknown==['Three-Implicit','Synthesised']:unknown=[];rule['special']='synthesised';rule['implicits']=3
        if unknown==['Area Level: 83','Fully Revealed']:unknown=[];rule['special']='blueprint'
        if unknown==['Implicit Modifier:','Corrupted Blood cannot be inflicted on you']:
            unknown=[];rule['special']='corrupted_blood'
        if r['reward_kind']=='magic' and not unknown:
            if r['card_name']=="Keeper's Corruption":
                label='Bone Helmet';rule['affix']='id:AreaDamageSupportedUber3'
            elif label.startswith('Life '):label=label[5:];rule['affix']='life'
            else:
                for affix in affixes:
                    if label.startswith(affix+' '):label=label[len(affix)+1:];rule['affix']=named_affix.get(affix,affix);break
                    if label.endswith(' '+affix):label=label[:-len(affix)-1];rule['affix']=named_affix.get(affix,affix);break
            if label=='Map':label='Metadata/Items/Maps/MapKeyTier16'
            members=base_members([byid[label]] if label in byid else category(label))
            if rule['special']=='elevated':members=[m for m in members if byid[m[1]]['Class'] in {'Body Armour','Helmet','Boots','Gloves'}]
            supported=bool(members)
        elif r['reward_kind'] in ('normal','rare') and not unknown:
            if label=='Blueprint':members=base_members([b for b in bases if b['Id'].startswith('Metadata/Items/Heist/HeistBlueprint')])
            elif label=='Elder Guardian Occupied Map':
                members=base_members([byid['Metadata/Items/Maps/MapKeyTier16']]);rule['special']='elder_guardian'
            elif label=='Map':
                tier=re.search(r'Map Tier: (\d+)',plain);tier=int(tier[1]) if tier else 16
                members=base_members([byid[f'Metadata/Items/Maps/MapKeyTier{tier}']])
            elif label in ('Scarab','Horned Scarab','Divination Scarab','Cartography Scarab'):
                members=base_members([byid[x] for x in scarabs if label=='Scarab' or byid[x]['Name'].startswith(label)])
            elif label in ['Sacrifice Fragment','Mortal Fragment','Uber Elder Fragment','Uber Pinnacle Fragment','Incarnation Echo']:
                names={'Sacrifice Fragment':['Sacrifice at Dawn','Sacrifice at Noon','Sacrifice at Dusk','Sacrifice at Midnight'],
                    'Mortal Fragment':['Mortal Grief','Mortal Hope','Mortal Rage','Mortal Ignorance'],
                    'Uber Elder Fragment':['Fragment of Knowledge','Fragment of Shape','Fragment of Terror','Fragment of Emptiness'],
                    'Uber Pinnacle Fragment':['Blazing Fragment','Devouring Fragment','Awakening Fragment','Reality Fragment','Decaying Fragment','Cosmic Fragment','Synthesising Fragment'],
                    'Incarnation Echo':['Echo of Trauma','Echo of Reverence','Echo of Loneliness']}
                members=base_members([b for b in bases if b['Name'] in names[label] and b['Id'].startswith('Metadata/Items/MapFragments/')])
            elif label=='Vaal Temple Map':members=base_members([byid['Metadata/Items/Maps/MapKeyVaalTemple']])
            else:members=base_members(category(label))
            if rule['special']=='perfect_eldritch':members=[m for m in members if byid[m[1]]['Class'] in {'Body Armour','Helmet','Boots','Gloves'}]
            if label=='Talisman':rule['special']='talisman';r['corrupted']=1
            supported=bool(members)
        elif r['reward_kind']=='currency' and not unknown:
            selected=[]
            if label=='Currency':
                ordinary={row[0] for row in world.execute('SELECT currency_id FROM drop_currency_weights WHERE enabled=1')}
                extra={'Orb of Binding','Orb of Unmaking','Orb of Horizons','Stacked Deck','Ancient Orb','Fracturing Orb','Sacred Orb',"Hinekora's Lock",'Enkindling Orb','Instilling Orb',
                    'Lesser Eldritch Ichor','Greater Eldritch Ichor','Grand Eldritch Ichor','Exceptional Eldritch Ichor','Lesser Eldritch Ember','Greater Eldritch Ember','Grand Eldritch Ember','Exceptional Eldritch Ember',
                    'Eldritch Chaos Orb','Eldritch Exalted Orb','Eldritch Orb of Annulment',"Veiled Chaos Orb",'Veiled Exalted Orb'}
                selected=[b for b in bases if b['Id'] in ordinary or b['Class']=='StackableCurrency' and b['Id'].startswith('Metadata/Items/Currency/') and (b['Name'] in extra or b['Id'].endswith('Shard') and b['Name'] not in ['Fracturing Shard'])]
            elif label in ['Essence','Shrieking Essence','Deafening Essence']:selected=[byid[x] for x in essences if label=='Essence' or byid[x]['Name'].startswith(label)]
            elif label=='Astrolabe':selected=[b for b in bases if b['Id'].startswith('Metadata/Items/Currency/Astrolabe')]
            elif label=='Vendor Refresh Currency':selected=[b for b in bases if b['Id'].startswith('Metadata/Items/Currency/CurrencyRefresh')]
            elif label=='Journey Tattoo':selected=[b for b in bases if b['Name'].startswith('Journey Tattoo')]
            elif label=='Incursion Vial':selected=[b for b in bases if b['Id'].startswith('Metadata/Items/Currency/CurrencyIncursionVial')]
            elif label=='Oil':selected=[b for b in bases if re.fullmatch(r'Metadata/Items/Currency/Mushrune\d+b?',b['Id'])]
            elif label=='Fossil':selected=[byid[x] for x in fossils]
            elif label=="Elderslayer's Exalted Orb":selected=[b for b in bases if b['Name'] in ["Crusader's Exalted Orb","Redeemer's Exalted Orb","Hunter's Exalted Orb","Warlord's Exalted Orb"]]
            elif label=='Quality Currency':selected=[b for b in bases if b['Name'] in ["Armourer's Scrap","Blacksmith's Whetstone","Glassblower's Bauble","Gemcutter's Prism","Cartographer's Chisel"]]
            members=base_members(selected);supported=bool(members)
            if label=='Currency':
                weights=dict(world.execute('SELECT currency_id,weight FROM drop_currency_weights WHERE enabled=1'))
                members=[(k,i,b,max(1,round(weights.get(i,1)*100))) for k,i,b,_ in members]
        elif r['reward_kind']=='unique' and not unknown:
            members=named([label])
            if label=="Torrent's Reclamation":members=named(["The Torrent's Reclamation"])
            if rule['special']=='primordial':members=named(['Primordial Might','Primordial Harmony','Primordial Eminence'])
            if label in ['Farrul Item','Doedre Item','Shavronne Item','Lioneye Item','Rigwald Item','Agnerod Staff']:
                prefix={'Farrul Item':"Farrul'",'Doedre Item':"Doedre'",'Shavronne Item':"Shavronne'",'Lioneye Item':"Lioneye'",'Rigwald Item':"Rigwald'",'Agnerod Staff':'Agnerod '}[label]
                members=named([u['name'] for u in unique_rows if u['name'].startswith(prefix)])
            if label=='Replica Item':members=named([u['name'] for u in unique_rows if u['name'].startswith('Replica ')])
            if label=='Synthesis Map':members=named(['Altered Distant Memory','Augmented Distant Memory','Twisted Distant Memory','Rewritten Distant Memory','Cortex'])
            if label=='Map':members=named(maps)
            if label=='Bestiary Item':members=named([u['name'] for u in unique_rows if any(u['name'].startswith(prefix) for prefix in ["Farrul'","Fenumus'","Craiceann'","Saqawal'"])])
            if label=='Breach Item':members=named(['Xoph\'s Inception','Xoph\'s Nurture','The Formless Flame','The Formless Inferno','Xoph\'s Heart','Xoph\'s Blood','Hand of Thought and Motion','Hand of Wisdom and Action','Esh\'s Mirror','Esh\'s Visage','Voice of the Storm','Choir of the Storm','Tulborn','Tulfall','The Snowblind Grace','The Perfect Form','The Halcyon','The Pandemonius','Uul-Netol\'s Kiss','Uul-Netol\'s Embrace','The Infinite Pursuit','The Red Trail','The Anticipation','The Surrender','Severed in Sleep','United in Dream','Skin of the Loyal','Skin of the Lords','The Red Dream','The Red Nightmare','The Green Dream','The Green Nightmare','The Blue Dream','The Blue Nightmare','Uul-Netol\'s Vow','The Hidden Blade'])
            special_names={
                'Fishing Item':["Song of the Sirens","Fairgraves' Tricorne",'Slitherpinch','Rainbowstride','The Writhing Jar'],
                'Delve Item':['The Grey Spire','Demon Stitcher','The Primordial Chain','Soulwrest','Mark of Submission'],
                'Chayula Item':['Severed in Sleep','United in Dream','Skin of the Loyal','Skin of the Lords','The Red Dream','The Red Nightmare','The Green Dream','The Green Nightmare','The Blue Dream','The Blue Nightmare'],
                'Beyond Item':['Edge of Madness','The Harvest','The Dark Seer'],
                'Metamorph Item':['Astral Projector','Fury Valve','The Black Cane','Warrior\'s Legacy','Mother\'s Embrace'],
                'Harbinger Item':['The Tempest\'s Binding','The Rippling Thoughts','The Enmity Divine','The Unshattered Will','The Flow Untethered','The Fracturing Spinner'],
                'Nemesis Item':["Berek's Grip","Berek's Pass","Berek's Respite",'Blood of the Karui','Headhunter',"Lavianga's Spirit"],
                'Maven Item':["Viridi's Veil",'Echoforge','Grace of the Goddess','Curio of Potential'],
                'Attribute Transforming Jewel':['Fertile Mind','Brute Force Solution','Careful Planning','Inertia','Efficient Training','Fluid Motion'],
                'Vaal Jewel':['Glorious Vanity','Tempered Flesh','Tempered Mind','Tempered Spirit','Transcendent Flesh','Transcendent Mind','Transcendent Spirit'],
                'Originator Incarnation Item':['Bonemeld','Legacy of the Rose','Servant of Decay','Starcaller',"The Arkhon's Tools","Enmity's Embrace"],
                'Synthesis Item':['Circle of Anguish','Circle of Fear','Circle of Guilt','Circle of Nostalgia','Circle of Regret',"Storm's Gift",'Bottled Faith','Garb of the Ephemeral']}
            if label in special_names:members=named(special_names[label])
            if label=='League-Specific Item':members=named([u['name'] for u in unique_rows if u['name'] in league_names and general(u)])
            if label=='Relic':members=named([u['name'] for u in unique_rows if u['base_type'].startswith('Metadata/Items/Relics/SanctumRelic')])
            if label=='Atziri Item':members=named([u['name'] for u in unique_rows if u['name'].startswith("Atziri's ") or u['name'] in ["Doryani's Catalyst","Doryani's Invitation",'The Vertex','Triumvirate Authority']])
            if label=='Vaal Aspect':members=base_members([b for b in bases if b['Id'].startswith('Metadata/Items/UniqueFragments/FragmentUniqueMap26_')]);rule['special']='vaal_aspect'
            if not members:
                ts={b['Id'] for b in category(label,unique=True)}
                specific=label not in generic_labels and any(b['Name']==label for b in bases)
                members=[('unique',u['name'],u['base_type'],max(1,round(unique_weights.get(u['name'],0.1)*1000000))) for u in unique_rows if u['base_type'] in ts and (specific or general(u)) and not u['name'].startswith('Replica ')]
            supported=bool(members)
        elif r['card_name']=='The Gambler':
            weights=dict(world.execute("SELECT item_id,weight FROM drop_item_weights WHERE category='card' AND enabled=1 AND weight>0"))
            members=[('base',c['card_id'],'',max(1,round(weights.get(c['card_id'],1)*10000))) for c in seed.execute("SELECT card_id FROM divination_card_rewards WHERE reward_kind!='disabled'")]
            supported=True
        elif r['card_name']=='The Void':
            r['reward_kind']='set_pool'
            members=[('set',c['card_id'],'',1) for c in seed.execute("SELECT card_id FROM divination_card_rewards WHERE reward_kind!='disabled' AND card_name!='The Void'")]
            supported=True
        if rule['influences'] or rule['random_influences']:
            members=[m for m in members if (byid.get(m[1]) if m[0]=='base' else byid.get(m[2])) is not None and (byid.get(m[1]) if m[0]=='base' else byid.get(m[2]))['Class'] in weapons|armour|jewellery|{'Quiver'}]
            supported=bool(members)
        if r['corrupted']:
            members=[m for m in members if m[0] not in ('unique','base') or 'Flask' not in (byid.get(m[1]) if m[0]=='base' else byid.get(m[2]))['Class']]
            supported=bool(members)
        if supported:
            sql.append('UPDATE divination_card_rewards SET enabled=1, reward_kind='+q(r['reward_kind'])+', corrupted='+str(r['corrupted'])+", source='3.29.3d descriptions with typed constraints and explicit pool members', notes='Typed advertised constraints and explicit pool members. Local selection weights with official exchange probabilities unrecovered.' WHERE card_id="+q(r['card_id'])+';')
            sql.append('INSERT INTO divination_reward_rules VALUES ('+', '.join(map(q,[r['card_id'],*rule.values()]))+');')
            for member in sorted(set(members)):sql.append('INSERT INTO divination_reward_members VALUES ('+', '.join(map(q,[r['card_id'],*member]))+');')
            if rule['special']=='synthesised':
                ids=sorted(RING_IMPLICITS)
                for mod in ids:
                    if db.execute('SELECT count(*) FROM Mods WHERE Id=? AND GenerationType=3',(mod,)).fetchone()[0]:
                        sql.append('INSERT INTO divination_reward_implicit_mods VALUES ('+', '.join(map(q,[r['card_id'],mod]))+');')
            enabled.append(dict(card=r['card_name'],members=len(members),rule=rule))
        else:remaining.append(dict(card=r['card_name'],kind=r['reward_kind'],label=label,unknown=unknown))
    seed.executescript("\n".join(sql))
    return seed, remaining


def unique_catalog(d):
    relics={
     'The Hour of Divinity':('1x2','SanctumUniqueRelic1x2',['DuplicateDeferrals1','CannotHaveBoons']),
     'The Gilded Chalice':('1x3','SanctumUniqueRelic1x3',['DuplicateDeferrals2','CannotHaveInspiration','CannotRecoverResolve']),
     'The Night Lamp':('2x1','SanctumUniqueRelic2x1',['AdditionalSanctumKey1','NoSanctumDefences']),
     'The Broken Censer':('3x1','SanctumUniqueRelic3x1_',['AdditionalSanctumKey2','MapIsHidden','LevelRequirement75']),
     'The First Crest':('2x2','SanctumUniqueRelic2x2',['ConvertAureusToExperience']),
     'The Second Sacrament':('1x4','SanctumUniqueRelic1x4',['LyciaDamage2','LyciaDamageTaken2','WindsOfFate','LevelRequirement83']),
     'The Original Scripture':('4x1','SanctumUniqueRelic4x1',['OriginalSin','CannotHaveInspiration','MaximumResolveIsOne','LevelRequirement83']),
     'The Blood of Innocence':('1x2','SanctumUniqueRelic1x2_2',['BalanceOfTerror','LyciaDamage1','LyciaDamageTaken1']),
     'The Chains of Castigation':('1x3','SanctumUniqueRelic1x3_2',['SandstormVisage','MapIsHidden','LevelRequirement75']),
     'The Power and the Promise':('3x1','SanctumUniqueRelic3x1_2_',['EternalDamnation','CannotHaveInspiration','CannotRecoverResolve','LevelRequirement80'])}
    def q(x):return "'"+x.replace("'","''")+"'"
    sql=['-- Unique identities outside UniqueStashLayout (Sanctum relics) use their current-client Words and artwork.',
         '-- Modifier membership: PoEDB item pages, cross-checked against pinned 3.29.3d Mods and base types.',
    '''CREATE TABLE unique_item_identities (
        name TEXT NOT NULL, base_type TEXT NOT NULL, visual_identity TEXT NOT NULL,
        PRIMARY KEY (name, base_type), FOREIGN KEY (name, base_type) REFERENCES unique_items(name, base_type)
    ) STRICT;''']
    for name,(shape,visual,suffixes) in relics.items():
        base='Metadata/Items/Relics/SanctumRelic'+shape
        assert d.execute('SELECT count(*) FROM Words WHERE Text=? AND Wordlist=6',(name,)).fetchone()[0]
        assert d.execute('SELECT count(*) FROM ItemVisualIdentity WHERE Id=?',(visual,)).fetchone()[0]
        assert d.execute('SELECT count(*) FROM BaseItemTypes WHERE Id=?',(base,)).fetchone()[0]
        sql.append('INSERT INTO unique_items (name,base_type,item_level,encounter_only,own_implicits) VALUES ('+','.join([q(name),q(base),'83','1','0'])+');')
        sql.append('INSERT INTO unique_item_identities VALUES ('+','.join(map(q,[name,base,visual]))+');')
        for order,suffix in enumerate(suffixes):
            mod='SanctumUniqueRelic'+suffix
            assert d.execute('SELECT count(*) FROM Mods WHERE Id=? AND Domain=7 AND GenerationType=3',(mod,)).fetchone()[0]
            sql.append('INSERT INTO unique_item_mods VALUES ('+','.join([q(name),q(base),'0',str(order),q(mod)])+');')
    name='Replica Cortex';base='Metadata/Items/Maps/MapKeyTier1';visual='UniqueMap25x'
    sql.append('INSERT INTO unique_items (name,base_type,item_level,encounter_only,own_implicits) VALUES ('+','.join([q(name),q(base),'84','1','0'])+');')
    sql.append('INSERT INTO unique_item_identities VALUES ('+','.join(map(q,[name,base,visual]))+');')
    for order,mod in enumerate(['SynthesisUniqueMapsModCortexReplica','MapMonsterLevelOverrideCortexReplica','MapAddsExtraSynthesisModsCortexReplica','MapAddsExtraSynthesisSpecialModsCortexReplica']):
        assert d.execute('SELECT count(*) FROM Mods WHERE Id=?',(mod,)).fetchone()[0]
        sql.append('INSERT INTO unique_item_mods VALUES ('+','.join([q(name),q(base),'0',str(order),q(mod)])+');')
    return "\n".join(sql)+"\n"
