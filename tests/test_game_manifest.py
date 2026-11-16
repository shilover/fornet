# 0.4.0 游戏配置由适配 manifest 给（GameInfo.Load）+ 基底的开局前存档备份（SaveBackup）。
# 用编好的基底联机程序（不带任何游戏适配）在临时模组目录里跑 --print-game，看读到的值。
#   python -X utf8 -I tests/test_game_manifest.py <基底联机程序.exe> <临时目录>
import os, shutil, subprocess, sys, zipfile

exe, work = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
fails = []


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("  " + str(detail) if detail else ""))
    if not ok:
        fails.append(name)


def mod(name):
    d = os.path.join(work, name)
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    shutil.copy(exe, os.path.join(d, "lobby.exe"))
    return d


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def show(d, backup=False):
    out = os.path.join(d, "game.txt")
    if os.path.exists(out):
        os.remove(out)
    args = [os.path.join(d, "lobby.exe"), "--print-game", out] + (["--backup-now"] if backup else [])
    subprocess.run(args, timeout=60)
    kv = {}
    if os.path.exists(out):
        for line in open(out, encoding="utf-8").read().splitlines():
            if "=" in line:
                k, v = line.split("=", 1)
                kv[k] = v
    return kv


# 1. 没有适配：中性默认值
d = mod("plain")
g = show(d)
check("没有 games\\：用默认值", g.get("source") == "default" and g.get("mod_name") == "fornet" and g.get("wire_peer") == "4N", g)
check("没有 games\\：不备份", g.get("backup") == "off", g)

# 2. 适配 manifest 给全部名字
d = mod("full")
write(os.path.join(d, "games", "demo", "manifest.txt"), "\n".join([
    "name=demo", "entry=adapter.lua",
    "mod_name=Demo_MP", "process=DemoGame.exe", "game_zh=演示游戏", "game_en=Demo Game",
    "title_zh=演示 联机", "title_en=Demo online", "app_version=1.2.3",
    "ranked_zh=天梯", "ranked_long_zh=天梯排位", "ranked_mode_zh=天梯（经大厅服务器）",
    "wire_server=DMS", "wire_peer=DM", "wire_salt=demo", "wire_env=DEMO_", "wire_abi=dmx_", "wire_pipe=demo_bridge_",
]) + "\n")
g = show(d)
want = {"source": "manifest:demo", "mod_name": "Demo_MP", "process": "DemoGame", "game_zh": "演示游戏", "game_en": "Demo Game",
        "title_zh": "演示 联机", "title_en": "Demo online", "app_version": "1.2.3",
        "ranked_zh": "天梯", "ranked_long_zh": "天梯排位", "ranked_mode_zh": "天梯（经大厅服务器）",
        "wire_server": "DMS", "wire_peer": "DM", "wire_salt": "demo", "wire_env": "DEMO_", "wire_abi": "dmx_", "wire_pipe": "demo_bridge_"}
bad = {k: (g.get(k), v) for k, v in want.items() if g.get(k) != v}
check("manifest 的键全部生效（process 去掉 .exe）", not bad, bad)

# 3. 只写一部分：没写的保持默认
d = mod("part")
write(os.path.join(d, "games", "demo", "manifest.txt"), "name=demo\nmod_name=Part_MP\n")
g = show(d)
check("只写 mod_name：其余默认", g.get("mod_name") == "Part_MP" and g.get("wire_peer") == "4N" and g.get("app_version") == "0.0.0", g)

# 4. games.txt 选适配；没有 games.txt 按目录名排序取第一个
d = mod("pick")
write(os.path.join(d, "games", "aaa", "manifest.txt"), "name=aaa\nmod_name=AAA\n")
write(os.path.join(d, "games", "bbb", "manifest.txt"), "name=bbb\nmod_name=BBB\n")
g = show(d)
check("没有 games.txt：取排第一的 aaa", g.get("mod_name") == "AAA", g)
write(os.path.join(d, "games", "games.txt"), "# 注释\n\nbbb\n")
g = show(d)
check("games.txt 选 bbb", g.get("mod_name") == "BBB" and g.get("source") == "manifest:bbb", g)

# 5. 存档备份：账号子目录布局，save_dir 相对模组目录
d = mod("save")
saves = os.path.join(work, "save_saves")
shutil.rmtree(saves, ignore_errors=True)
write(os.path.join(saves, "111", "Game.sav"), "v1")
write(os.path.join(saves, "111", "sub", "extra.bin"), "x")
write(os.path.join(saves, "222", "other.txt"), "no save file here")
write(os.path.join(d, "games", "demo", "manifest.txt"),
      "name=demo\nsave_dir=..\\save_saves\nsave_file=Game.sav\nsave_backup_dir=my-backups\n")
g = show(d, backup=True)
outd = os.path.join(saves, "my-backups")
zips = sorted(os.listdir(outd)) if os.path.isdir(outd) else []
check("备份开着", g.get("backup") == "on", g)
check("只备份有存档文件的账号 111", len(zips) == 1 and zips[0].startswith("111-backup-") and zips[0].endswith("-mp.zip"), zips)
if zips:
    names = zipfile.ZipFile(os.path.join(outd, zips[0])).namelist()
    check("备份里有存档文件和子目录", "Game.sav" in names and "sub/extra.bin" in names, names)
g = show(d, backup=True)
zips2 = sorted(os.listdir(outd))
check("存档没变：第二次不再备份", len(zips2) == 1 and "一样" in g.get("backup_result", "") or "unchanged" in g.get("backup_result", ""), (zips2, g.get("backup_result")))
write(os.path.join(saves, "111", "Game.sav"), "v2")
import time; time.sleep(1.1)
g = show(d, backup=True)
check("存档变了：再备份一份", len(os.listdir(outd)) == 2, os.listdir(outd))

# 6. 存档文件直接放在 save_dir 里（没有账号子目录）、不写 save_file
d = mod("flat")
flat = os.path.join(work, "flat_saves")
shutil.rmtree(flat, ignore_errors=True)
write(os.path.join(flat, "slot1.sav"), "a")
write(os.path.join(d, "games", "demo", "manifest.txt"), "name=demo\nsave_dir=" + flat + "\n")
g = show(d, backup=True)
fz = os.listdir(os.path.join(flat, "fornet-save-backups")) if os.path.isdir(os.path.join(flat, "fornet-save-backups")) else []
check("平铺布局 + 绝对路径 + 默认备份目录", len(fz) == 1 and fz[0].startswith("flat_saves-backup-"), (fz, g.get("backup_result")))
if fz:
    names = zipfile.ZipFile(os.path.join(flat, "fornet-save-backups", fz[0])).namelist()
    check("平铺布局的备份不把备份目录自己打进去", names == ["slot1.sav"], names)

print("FAIL %d" % len(fails) if fails else "全部通过")
sys.exit(1 if fails else 0)
