import sqlite3

c = sqlite3.connect(r"D:\大肥鱼工作区\LocalExile\data\saves.db")
tables = [r[0] for r in c.execute("select name from sqlite_master where type='table'")]
print("tables:", tables)
for t in tables:
    if "account" in t.lower():
        cols = [d[1] for d in c.execute("pragma table_info(%s)" % t)]
        print(t, cols)
        for row in c.execute("select * from %s" % t):
            print("  ", row[0], "|", row[1][:40] if len(row) > 1 and isinstance(row[1], str) else "")
